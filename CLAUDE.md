# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

A data repository, not a code repository. It holds the inputs that reproduce the CMMS load-test datasets
exactly (`profiles/`) and a **read-only reference snapshot** of the source that produces them (`tools/`,
plus `infra/`, `scripts/`, `tests/`). The datasets themselves live in `data/`, which is generated locally
and not committed.

The product lives in [`cmms`](https://github.com/lnicoara/cmms). Nothing here is built or deployed.

## Hard rules

1. **Never edit `tools/`, `infra/`, or `scripts/` here.** They are snapshots taken from `cmms`. Both
   `tools/` projects carry a `ProjectReference` to `../../src/Cmms.Infrastructure`, a path that does not
   exist in this repo, so they cannot build here and a `dotnet build` fails immediately. That coupling is
   deliberate, not debt: `ModelBridge` builds a real `CmmsDbContext` over a never-opened connection so the
   artifact's column manifest is derived from the live EF model, and the loader reuses the same
   `ModelBridge` so generator and loader cannot drift on a migration. Forking the source would make it agree
   with itself and disagree with the database, surfacing as a corrupt bulk load rather than a build error.
   Read the snapshot here; change it in `cmms`. See [tools/README.md](tools/README.md).
2. **`data/` is never committed.** It is in `.gitignore` and absent from a fresh clone. `profiles/` is the
   only authoritative thing here, because generation is a pure function of the profile: the profile is
   right and a disagreeing `data/` is stale, always. Regenerate rather than reconcile.
3. **Never reuse a seed across profiles.** Primary keys are a pure function of `(seed, kind, ordinal)` with
   no count input, so a smaller run on a larger profile's seed produces keys that are a strict subset of the
   larger artifact's. The loader's chunk-presence probe is a bare primary-key lookup, so it would adopt the
   wrong rows and write checkpoints neither the checkpoint store nor the target cleaner can delete,
   permanently poisoning the artifact against that database.
4. **A local `data/` carries no provenance.** Nothing in a clone records which profile produced the
   directory sitting on your disk, and a stale `full/` looks exactly like a fresh one. Read
   `manifest.json`'s seed and `totalRows` before trusting a dataset you did not just generate; the full
   run takes about an hour, which is enough incentive to reuse the wrong one.
5. **A load never decides or changes the target's schema** (`lnicoara/cmms#2978`). Which schema an
   environment runs is settled by promoting a commit, and a data load must not be the thing that settles it.
   The generator records the migration ids its model carried; the loader compares them to what the target
   has applied and declines on a mismatch, naming both. If they disagree, regenerate against the commit the
   target runs. Never migrate the target to fit an artifact. See [docs/load.md](docs/load.md).

## Commands

There is no build, lint, or unit-test step in this repository. What runs here:

```bash
# The one runnable test: static conformance over the load delivery chain. Azure-free, reads scripts
# and Bicep rather than calling Azure.
bash tests/infra/load-job-conformance.test.sh

# Confirm a dataset's identity before trusting it
python3 -c "import json;d=json.load(open('data/small/manifest.json'));print(d['seed'], d['totalRows'])"
```

Generation and loading run **from a `cmms` checkout**, not from here:

```bash
# Regenerate (cmms repo root). ~15 s for small, ~1 h for full.
GENERATOR_PARAMS=small.json dotnet run --project tools/Cmms.LoadDataGenerator -c Release -- --OutDir=/tmp/gen-small

# Load into a pre-prod tenant. Plan mode is the default and writes nothing.
ARTIFACT_DIR=/path/to/cmms-data/data/small scripts/pre-prod/load-tenant.sh
ARTIFACT_DIR=/path/to/cmms-data/data/small scripts/pre-prod/load-tenant.sh --execute --clear-target=<slug>
```

**`GENERATOR_PARAMS` resolves relative to the project directory, not the repo root.** A path that does not
resolve is added with `optional: true` and skipped in silence, so the run proceeds on `GeneratorOptions`
defaults, whose `Seed` is `cmms-loadtest-v1` — the **full** profile's seed. It prints a banner, validates,
and reports success while producing exactly the key-collision hazard rule 3 exists to prevent. Always check
the seed in the manifest. Details in [docs/regenerate.md](docs/regenerate.md).

## Artifact shape

```
data/<profile>/
  manifest.json   formatVersion, seed, generatedTables, rowsPerChunk, totalRows, the full parameter set,
                  and the schema it was built against: sourceCommit, modelSourcesDirty, latestMigration,
                  modelMigrations
  stats.json      observed-vs-target distribution checks
  run.json        elapsed seconds, peak working set
  <Table>/<Table>-NNNNN.jsonl.gz
```

`formatVersion: v1` is a contract, not a description: **every non-final chunk of a table holds exactly
`rowsPerChunk` rows, and only the final chunk may be short.** That is what lets the loader derive exact
per-chunk row counts from twelve file reads instead of 438, and cross-check the derived sum against
`manifest.totalRows` — which catches a missing chunk, a misnamed file, or a truncated download for free. A
writer that rolled on compressed size would be v2, not a compatible change.

## Design invariants worth knowing before you touch anything

- **Determinism.** `Deterministic` is a stateless SHA-256 counter-hash, never `Random` — stateless so any
  row is derivable from its coordinates (shardable, resumable, identical), SHA-256 because `Random`'s
  sequence carries no cross-version stability guarantee and an SDK bump would silently change the artifact.
- **Reference cardinality does not scale down.** `small` and `full` carry identical reference and lookup
  tables (AssetTypes, Companies, Models, Accounts, ServiceLines, AccessGroups, Users, Labor). Volume scales;
  cardinality does not, because scaling reference data down makes indexes get ignored in favour of scans and
  the load test then measures scans where production seeks. The corollary: **`small` is not a
  density-faithful sample** — use it to prove a pipeline, never to draw an index or query-plan conclusion.
- **The loader refuses far more often than it fails,** and every refusal is deliberate: a row-count deficit,
  an artifact generated against migrations the target does not have, a connection secret naming another
  database or another environment's server, a `--clear-target` slug that does not match the target tenant.
  The alternative to each refusal is a plausible-looking wrong dataset. What no refusal ever does is change
  the target: it reports both versions and stops.
- **Clearing removes 136 of 162 tables** (the transitive closure of everything referencing the artifact's
  write set) **including every user**, which leaves the tenant unreachable through normal login. Recovery is
  the seed job's `ensureAdmin` mode. See [docs/load.md](docs/load.md), which also lists the known operational
  problems a smaller dataset does not fix.
- **Three profiles exist: `smoke`, `small`, `full`.** None of them has a committed dataset; whatever is in
  your local `data/` is whatever you last generated.

## Writing prose here

Every README, doc, and source comment in this repository states the *reason* for a decision and names the
failure it prevents, usually with a `lnicoara/cmms#NNNN` issue reference. Match that. Do not add comments
that restate what the code does.
