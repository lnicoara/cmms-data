# cmms-data

Generated load-test datasets for the CMMS platform, and the profiles that reproduce them.

The product lives in [`cmms`](https://github.com/lnicoara/cmms). This repository holds the **output** of its
load-test data generator plus the inputs needed to regenerate that output exactly. Nothing here is built or
deployed.

## Layout

```
profiles/     the inputs. Each JSON file fully determines one dataset.
data/         the outputs. One directory per profile run.
  small/        250,869 rows,      22 MB,  63 files
  full/      42,738,899 rows,     3.7 GB, 441 files
tools/        REFERENCE SNAPSHOT of the four .NET tools. See the warning below.
  Cmms.LoadDataGenerator/   produces an artifact, opens no database
  Cmms.LoadDataRunner/      bulk-copies an artifact into one tenant database
  Cmms.MigrationRunner/     applies EF migrations, which the loader requires to be current
  Cmms.TenantSeedRunner/    demo seed, and the ensureAdmin recovery after a clear
scripts/      REFERENCE SNAPSHOT of the operator scripts, and the order to run them in.
infra/        the Bicep that stands up the load, migrate and seed jobs and the artifact store.
tests/        the Azure-free conformance suite over the delivery chain.
docs/         how to regenerate, and how to load.
```

The whole chain is here, but only `profiles/` and `data/` are authoritative. Everything under `tools/`,
`scripts/`, `infra/` and `tests/` is a copy taken from `cmms`, for reading. `scripts/README.md` covers the
running order and why it is not optional.

## Two things to know before you rely on this

**1. The data is reproducible, so the profiles matter more than the bytes.** Generation is a pure function
of the profile: the same seed and parameters produce a byte-identical artifact, which is asserted by a test
in `cmms` (`Same_seed_produces_a_byte_identical_artifact`). Primary keys are derived from
(seed, kind, ordinal) with no randomness and no clock. If `data/` and `profiles/` ever disagree, the profile
is right and the data is stale. Regenerating the small run takes about 15 seconds; the full run takes about
an hour.

**2. `tools/` is a snapshot and does not build here.** Both projects carry a `ProjectReference` to
`src/Cmms.Infrastructure` in the `cmms` repository, which pulls in `Cmms.Application` and `Cmms.Domain`.
That is not incidental coupling to be refactored away: `ModelBridge` builds a real `CmmsDbContext` over a
never-opened connection so the artifact's column manifest is derived from the live EF model, and the loader
reuses the same `ModelBridge` so the two cannot drift on a migration. A forked copy would desynchronise
silently the next time the schema moved, and the failure would surface as a corrupt bulk load rather than a
build error. Read the snapshot; change it in `cmms`.

## The datasets

| | small | full |
|---|---:|---:|
| Total rows | 250,869 | 42,738,899 |
| On disk | 22 MB | 3.7 GB |
| Chunk files | 63 | 441 |
| Seed | `cmms-loadtest-small-v1` | `cmms-loadtest-v1` |
| Generation time | ~15 s | ~1 h |

Per table:

| Table | small | full |
|---|---:|---:|
| AuditEvents | 195,593 | 34,317,200 |
| WorkOrders | 22,000 | 4,000,000 |
| WorkOrderLabor | 18,692 | 3,400,627 |
| Models | 6,750 | 6,750 |
| Assets | 6,096 | 1,012,584 |
| Labor | 500 | 500 |
| Users | 500 | 500 |
| Companies | 450 | 450 |
| AssetTypes | 224 | 224 |
| Accounts | 60 | 60 |
| ServiceLines | 2 | 2 |
| AccessGroups | 2 | 2 |

Reference and lookup tables are **identical in both**, and that is the design. Volume scales; cardinality
does not. Scaling reference data down would spread a small asset count over a handful of rooms and models,
and any index whose leading columns select an eighth of a table gets ignored in favour of a scan, so the
load test would measure scans where production seeks.

The small dataset is **not a density-faithful sample**. It holds full reference cardinality against a
hundredth of the volume, so assets per room and assets per model are far below production shape. Use it to
prove a pipeline, never to draw an index or query-plan conclusion.

## Why the seeds differ

They must. Primary keys are a pure function of (seed, kind, ordinal) with no count input, so a smaller run
on the full profile's seed yields keys that are a strict **subset** of the full artifact's. The loader's
chunk-presence probe is a bare primary-key lookup, so it would classify the full artifact's rows as the
small one's, adopt them, and write checkpoints that neither the checkpoint store nor the target cleaner can
delete. That poisons the artifact permanently against that database.

## Git LFS

`*.jsonl.gz` is tracked by LFS (see `.gitattributes`). Clone with LFS installed or `data/` arrives as
pointer files:

```bash
git lfs install
git clone <this repo>
```

The full dataset alone is 3.7 GB. If this repository is ever pushed to GitHub, note that LFS storage and
bandwidth are metered, with a small free tier. Regenerating from `profiles/full.json` is free and gives a
byte-identical result, so hosting the full dataset is a convenience rather than a necessity.

## See also

- `docs/regenerate.md`: producing either dataset from its profile
- `docs/load.md`: loading a dataset into a tenant database
