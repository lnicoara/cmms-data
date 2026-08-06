# Operator scripts

Snapshots of the scripts that stage and load a dataset. Like `tools/`, these are **reference copies**: they
run from a `cmms` checkout, because they build container images from that source tree and deploy Bicep
templates by relative path. Run them there, read them here.

## The order, which is not optional

1. **Migrate the target.** The loader refuses a database with pending migrations, and it refuses from inside
   the job, after the artifact has been staged. `load-tenant.sh` now does this first for exactly that reason.
2. **Stage and load.** `load-tenant.sh` deploys the store, uploads the artifact, builds the load image,
   deploys the job by digest, and starts it.
3. **Restore access if you cleared.** Clearing empties `Users`, so the tenant becomes unreachable through
   the normal login. `seed-tenant.sh` in `ensureAdmin` mode recreates a System Admin, its access group, and
   the MAIN service line, and refuses if any user still exists.

## scripts/pre-prod/load-tenant.sh

Plan by default, writes nothing. `--execute` loads. `--clear-target=<slug>` is a second, separate yes and the
slug must match the tenant being emptied.

```bash
ARTIFACT_DIR=/path/to/cmms-data/data/small scripts/pre-prod/load-tenant.sh
ARTIFACT_DIR=/path/to/cmms-data/data/small scripts/pre-prod/load-tenant.sh --execute --clear-target=demo-health
```

Its preflights exist because each one has already failed a real run: a dirty tree scoped to the paths that
reach the image, the tenant database existing, the artifact matching this checkout's model, the migrate job
carrying a current image, and the deployed job carrying the digest just built.

## scripts/pre-prod/seed-tenant.sh

Seeds demo data, or restores access. The recovery path matters more than it looks: pre-prod SQL denies public
network access, and the admin API sits behind interactive Entra that the Azure CLI has no consent for, so an
in-VNet job is the only way to write that row.

## infra/

| template | what it stands up |
|---|---|
| `load-job.bicep` | `caj-cmms-load`, the in-VNet job that reads the artifact and bulk-copies it |
| `migrate-job.bicep` | `caj-cmms-migrate`, EF migrations across catalog and every tenant database |
| `seed-job.bicep` | `caj-cmms-seed`, demo seed and the `ensureAdmin` recovery |
| `modules/load-test-store.bicep` | the storage account holding the staged artifact and the checkpoints |

Regions and resource names are **passed in**, not computed from `uniqueString()`. Two real failures taught
that: a job cannot be relocated by redeploy (`InvalidResourceLocation` when a template defaulted to
`eastus2` against a `centralus` environment), and a computed SQL server name resolved to a server that does
not exist.

## tests/infra/load-job-conformance.test.sh

Static, Azure-free assertions over the delivery chain. Several are behavioural rather than greps, because a
grep for wiring passes while the wiring is unreadable at the far end. It extracts and runs the entrypoint's
`truthy()` against Bicep's literal `string(true)` output, and renders the closing heredoc under
`set -euo pipefail`.

Run from a `cmms` checkout:

```bash
bash tests/infra/load-job-conformance.test.sh
```
