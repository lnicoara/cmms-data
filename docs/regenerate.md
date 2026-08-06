# Regenerating a dataset

`data/` is not committed, so this is not an optional maintenance chore: it is how you get a dataset at all.
The datasets are produced by `tools/Cmms.LoadDataGenerator` in the `cmms` repository. The generator opens no
database and reads no Azure configuration, so it runs on a laptop with only the .NET 8 SDK. That is
deliberate, and it is why generating and loading are separate steps.

## The commands

Run from the root of the `cmms` checkout, not from this repository.

```bash
# small: 250,869 rows, about 15 seconds
GENERATOR_PARAMS=small.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release \
  -- --OutDir=/tmp/gen-small

# full: 42,738,899 rows, about an hour
GENERATOR_PARAMS=full.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release \
  -- --OutDir=/tmp/gen-full
```

## Generate against the schema you intend to load into

The generator derives its columns from the EF model of **whatever commit is checked out**, and it never
contacts the target. That is what lets it run on a laptop, and it is also why the checkout you generate from
decides which databases the artifact fits.

An artifact built from `main` fits a database that has every migration through `main`. It does not fit
pre-prod unless pre-prod has been promoted that far, and that gap is not hypothetical: pre-prod was measured
running a commit whose model lacked two columns the `small` artifact carried
(`WorkOrders.ServiceProviderDate` and `Users.ArtifactsAdoptedAtUtc`).

What the target runs, read from the deployed image tag:

```bash
IMG=$(az containerapp show -n ca-cmms-api -g rg-cmms-preprod \
        --query "properties.template.containers[0].image" -o tsv)
az acr manifest list-metadata --registry <acr> --name cmms-api \
  --query "[?digest=='${IMG##*@}'].tags" -o tsv     # the tag IS the commit
```

Check out that commit before generating, and the artifact records it:

```json
"sourceCommit": "b518c6274d1d46e402fd831369da997c445d7a5e",
"modelSourcesDirty": false,
"latestMigration": "20260805233234_UserArtifactsAdoptedAtUtc",
"modelMigrations": ["...", "..."]
```

`modelMigrations` is the load-bearing field. A commit is not readable from a database, which records
migration ids and nothing else, and a commit that touches no migration produces an artifact fitting exactly
the same schemas. The commit is provenance for a human; the migration list is what the loader compares.

Two guards follow from this, both of which fail before writing anything:

- Generation **refuses** when it cannot determine the commit, rather than writing an artifact that cannot say
  what it fits. Outside a git checkout, name it: `--SourceCommit=<sha>`.
- `modelSourcesDirty` is true when `src/Cmms.Domain` or `src/Cmms.Infrastructure` has uncommitted edits, which
  means the recorded commit is not the whole truth about the model. Scoped to those two directories on
  purpose: editing a script or the generator itself does not change the model.

If the artifact and the target disagree, regenerate. Do not migrate the target to fit the artifact. See
[load.md](load.md).

## GENERATOR_PARAMS is resolved relative to the PROJECT, not the repo root

This is the trap, and it has already produced a wrong artifact once.

`GENERATOR_PARAMS=small.json` works. `GENERATOR_PARAMS=tools/Cmms.LoadDataGenerator/small.json` does **not**:
the configuration builder adds the file with `optional: true`, so a path that does not resolve is skipped in
silence and the run proceeds on `GeneratorOptions` defaults. It still prints a banner, still validates, and
still reports success.

The result is worse than a crash. The defaults carry `Seed = "cmms-loadtest-v1"`, which is the **full**
profile's seed, so the artifact inherits exactly the key-collision hazard the separate seeds exist to
prevent, while looking like a legitimate small run.

Always check the banner and the manifest before trusting an artifact:

```bash
head -1 <outdir>/manifest.json   # or:
python3 -c "import json;d=json.load(open('<outdir>/manifest.json'));print(d['seed'], d['totalRows'])"
```

Expected:

| profile | seed | totalRows |
|---|---|---:|
| small | `cmms-loadtest-small-v1` | 250,869 |
| full | `cmms-loadtest-v1` | 42,738,899 |

A small run reporting `cmms-loadtest-v1` did not load its profile. Delete it and start again.

## Verifying a regenerated dataset

The generator self-validates at the end of every run and prints 27 checks. All must read `[ok]`; the process
exits non-zero otherwise. The checks that matter most, because nothing downstream re-checks them:

- every expected table is present and non-empty (21 of the other checks are violation counters and would
  pass on a table that was never written)
- every access-group location pin resolves to an emitted location, and the pinned subtree contains equipment
- work-order numbers are dense 1..N per service line
- every foreign key that the artifact carries resolves inside the artifact

Byte-identity across runs is asserted by a test in `cmms`, so a regenerated dataset should match the one
committed here exactly. If it does not, the profile or the model changed, and the committed data is stale.

## Changing a profile

Edit the JSON in `profiles/`, copy it to `tools/Cmms.LoadDataGenerator/` in the `cmms` checkout, regenerate,
and commit both the profile and the data together. A profile and its data must never be committed apart.

Every cardinality option must be positive. The generator validates all twelve before it deletes the output
directory, so a bad profile now fails without destroying the previous artifact.
