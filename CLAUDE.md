# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

The load-test tooling and the inputs that reproduce its datasets. It **owns** the generator, the loader,
their job IaC, the operator scripts and the conformance suite; it builds and it runs. The datasets live in
`data/`, generated locally and never committed.

The product lives in [`cmms`](https://github.com/lnicoara/cmms). This repository consumes it as a package.

Until lnicoara/cmms#3026 the `tools/` here were a read-only snapshot copied from `cmms`, under a rule
saying never to edit them. **That rule is reversed. This is the source now.** If you find a doc still
saying otherwise, the doc is wrong.

## The rule everything else follows

**We target what is DEPLOYED TO PRE-PROD, the model and the database. We do not track the latest in
`main`.**

Every comparison means that. When something is described as behind, ahead, stale, or drifted, the
reference is the commit pre-prod runs. Measuring against `main` produces a number about nothing, because
`main` is not what the load runs against and not what the data has to fit.

## Hard rules

1. **`<CmmsVersion>` in [Directory.Build.props](Directory.Build.props) is the pin, and it names a commit.**
   `cmms` publishes itself as `Cmms.Infrastructure 1.0.0-g<shortsha>`, one version per commit on `main`,
   so referencing it here is referencing a build. Never float it to a wildcard or bump it because a
   restore complained: a newer package is a different model, and an artifact generated against it stops
   fitting the target with nothing saying so. `TargetBuild` compares the commit carried by the **loaded**
   `Cmms.Infrastructure` assembly against pre-prod's schema commit and refuses on a mismatch.
2. **Cmms.Infrastructure is a package, never a source copy.** `ModelBridge` builds a real `CmmsDbContext`
   for two reasons and only one is metadata: the column manifest is derived from the live EF model, and
   the resulting `EntityEntry` lets audit rows run through the product's own `AuditDiff` and
   `AuditEventFactory` (#2903) rather than a second implementation. A fork would agree with itself and
   disagree with the database, surfacing as a corrupt bulk load rather than a build error.
3. **`data/` is never committed.** It is gitignored and absent from a fresh clone. `profiles/` is
   authoritative because generation is a pure function of the profile: the profile is right and a
   disagreeing `data/` is stale, always. Regenerate rather than reconcile. `profiles/` is also the ONLY
   copy; a duplicate beside the generator fails CI.
4. **Never reuse a seed across profiles.** Primary keys are a pure function of `(seed, kind, ordinal)` with
   no count input, so a smaller run on a larger profile's seed produces keys that are a strict subset of
   the larger artifact's. The loader's chunk-presence probe is a bare primary-key lookup, so it would adopt
   the wrong rows and write checkpoints nothing can delete, permanently poisoning the artifact against that
   database.
5. **A local `data/` carries no provenance.** Nothing in a clone records which profile produced the
   directory on your disk, and a stale `full/` looks exactly like a fresh one. Read `manifest.json`'s seed
   and `totalRows` before trusting a dataset you did not just generate.
6. **A load never decides or changes the target's schema** (#2978). Which schema an environment runs is
   settled by promoting a commit. The load script does not build, deploy, or start the migration job, and a
   conformance test fails if that comes back. If a load is refused for pending migrations, run the
   migration deliberately; never migrate a target to fit an artifact.
7. **The load deletes nothing** (#3025). It is named for loading. `--clear-target` is gone and the script
   refuses it with an explanation. The loader loads on top of whatever is already there, which for a load
   test is more rows under load and therefore the point.

## Commands

```bash
# Build and test. Needs a token that can read Cmms.* from GitHub Packages:
#   dotnet nuget update source github -u <you> -p <PAT with read:packages> --store-password-in-clear-text
dotnet build Cmms.Data.sln -c Release
dotnet test  Cmms.Data.sln -c Release

# Azure-free static check over the whole delivery chain
bash tests/infra/load-job-conformance.test.sh

# Generate. One command; it derives the SDK, pins the profile, and verifies the artifact.
scripts/pre-prod/generate-pre-prod-small.sh                 # to $HOME/git/cmms-data/data/small
scripts/pre-prod/generate-pre-prod-small.sh --out=/abs/path

# Load. It LOADS; --plan is the dry run.
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small
scripts/pre-prod/load-pre-prod.sh --artifact-dir=... --plan

# Confirm a dataset's identity before trusting it
python3 -c "import json;d=json.load(open('data/small/manifest.json'));print(d['seed'], d['totalRows'])"
```

## Artifact shape

```
data/<profile>/
  manifest.json   formatVersion, seed, generatedTables, rowsPerChunk, totalRows, the full parameter set,
                  and the build it was generated for: sourceCommit and the resolved target
  stats.json      observed-vs-target distribution checks
  run.json        elapsed seconds, peak working set
  <Table>/<Table>-NNNNN.jsonl.gz
```

`formatVersion: v1` is a contract, not a description: **every non-final chunk of a table holds exactly
`rowsPerChunk` rows, and only the final chunk may be short.** That is what lets the loader derive exact
per-chunk row counts from a handful of file reads instead of hundreds, and cross-check the derived sum
against `manifest.totalRows`, which catches a missing chunk, a misnamed file, or a truncated download for
free. A writer that rolled on compressed size would be v2, not a compatible change.

## Design invariants worth knowing before you touch anything

- **Determinism.** `Deterministic` is a stateless SHA-256 counter-hash, never `Random` — stateless so any
  row is derivable from its coordinates (shardable, resumable, identical), SHA-256 because `Random`'s
  sequence carries no cross-version stability guarantee and an SDK bump would silently change the artifact.
  The move to this repository was verified against exactly this: the artifact produced here is byte-identical
  to the one `cmms` produced.
- **The artifact claims no value the target already owns.** It emitted `ServiceLines` at `ServiceLineSeed`'s
  Guids and at codes `CE`/`FE`, colliding on the primary key and on the unique `IX_ServiceLines_Code`. One of
  those Guids is inserted by a *migration* into every tenant database, so an unseeded tenant collided too.
  Both are seed-derived now. Any new hardcoded id or natural key is this bug again.
- **Reference cardinality does not scale down.** `small` and `full` carry identical reference and lookup
  tables. Volume scales; cardinality does not, because scaling reference data down makes indexes get ignored
  in favour of scans and the load test then measures scans where production seeks. The corollary: **`small`
  is not a density-faithful sample** — use it to prove a pipeline, never to draw an index or query-plan
  conclusion.
- **The loader refuses far more often than it fails,** and every refusal is deliberate: a row-count deficit
  against its own checkpoints, a partially-present chunk, a connection secret naming another database or
  another environment's server, a pinned version that disagrees with the target's schema. What no refusal
  ever does is change the target: it reports both versions and stops. Rows it did not write are NOT a
  refusal (#3025) — that rule made a target loadable only after being emptied.
- **The feed credential never enters the image.** The runner's image restores from a private feed, so the
  token reaches the remote build as a `--secret-build-arg` and a BuildKit secret mount, never a `--build-arg`
  or an `ARG`, either of which is recorded in image history. Both halves are asserted by the conformance suite.
- **Three profiles exist: `smoke`, `small`, `full`.** None has a committed dataset; whatever is in your local
  `data/` is whatever you last generated.

## Writing prose here

Every README, doc, and source comment in this repository states the *reason* for a decision and names the
failure it prevents, usually with a `lnicoara/cmms#NNNN` issue reference. Match that. Do not add comments
that restate what the code does.
