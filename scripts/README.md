# Operator scripts

These are the scripts, not copies of them. They run **from this repository** (lnicoara/cmms#3026); until
that change they were reference copies that ran from a `cmms` checkout, because the tools could not build
here.

## scripts/pre-prod/generate-pre-prod-small.sh

Produces the `small` dataset. One command, and that is the point: the invocation it replaces was four
things to get right together, and the worst of them fails silently. A missing `GENERATOR_PARAMS` runs on
`GeneratorOptions` defaults, whose seed is the **full** profile's, producing a small artifact whose key
space is a strict subset of the full one's. It prints a banner, self-validates, and reports success while
creating exactly the key-collision hazard the seed rule exists to prevent.

```bash
scripts/pre-prod/generate-pre-prod-small.sh                 # to $HOME/git/cmms-data/data/small
scripts/pre-prod/generate-pre-prod-small.sh --out=/abs/path
```

It derives the SDK onto `PATH`, pins the profile, writes the output directory (replacing what is there,
which is what generating means), and verifies the artifact against the pinned model before finishing. That
last check is free here and costs a multi-gigabyte upload and an image build when the load job discovers it
instead.

## scripts/pre-prod/load-pre-prod.sh

**It loads.** Typing the name is the intent, so there is no second flag confirming it. `--plan` is the dry
run: it stands everything up, verifies the staged artifact, and writes nothing. `--execute` is still
accepted and means nothing new, because every existing note says it.

```bash
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small --plan
```

**It deletes nothing** (lnicoara/cmms#3025). `--clear-target` is removed and refused with an explanation
rather than silently ignored, so an old command from shell history does not read as a load that cleared
first. See [docs/load.md](../docs/load.md) for what it used to do and why the collision it existed for is
fixed at the generator instead.

**It does not touch the schema** (lnicoara/cmms#2978). It no longer builds, deploys, or starts the
migration job. That step ran on every invocation and is why pre-prod's schema sat 85 commits ahead of the
application it was serving: a load test was deciding what the environment ran. The conformance suite fails
if it comes back. If a load is refused for pending migrations, run the migration deliberately.

Building the image needs a token that can read `Cmms.*` from GitHub Packages. The script reads
`gh auth token` and falls back to `NUGET_TOKEN`, and passes it as a `--secret-build-arg` so it is not
recorded in image history.

Its preflights exist because each has already failed a real run: a dirty tree scoped to the paths that
reach the image, the tenant database existing, the artifact matching the pinned model, the staged copy
being the one just uploaded, and the deployed job carrying both the digest just built and no clear target.

## infra/

| template | what it stands up |
|---|---|
| `load-job.bicep` | `caj-cmms-load`, the in-VNet job that reads the artifact and bulk-copies it |
| `modules/load-test-store.bicep` | the storage account holding the staged artifact and the checkpoints |

`migrate-job.bicep` and `seed-job.bicep` are **not** here. They are the deploy path and live in `cmms`.

Regions and resource names are **passed in**, not computed from `uniqueString()`. Two real failures taught
that: a job cannot be relocated by redeploy (`InvalidResourceLocation` when a template defaulted to
`eastus2` against a `centralus` environment), and a computed SQL server name resolved to a server that does
not exist.

## tests/infra/load-job-conformance.test.sh

Static, Azure-free assertions over the delivery chain. Several are behavioural rather than greps, because a
grep for wiring passes while the wiring is unreadable at the far end: it extracts and runs the entrypoint's
`truthy()` against Bicep's literal `string(true)` output.

```bash
bash tests/infra/load-job-conformance.test.sh
```
