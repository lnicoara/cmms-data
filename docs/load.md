# Loading a dataset

Loading happens from the `cmms` repository, not this one. This page records what the loader assumes about a
dataset in `data/`, and the known problems that are not fixed by using a smaller one.

## The command

```bash
# from the cmms checkout, logged in to the target subscription
ARTIFACT_DIR=/path/to/cmms-data/data/small \
  scripts/pre-prod/load-tenant.sh                          # PLAN: stages and verifies, writes nothing

ARTIFACT_DIR=/path/to/cmms-data/data/small \
  scripts/pre-prod/load-tenant.sh --execute --clear-target=<slug>
```

Plan mode is the default and writes nothing. `--execute` loads. `--clear-target=<slug>` is a second,
separate yes: it empties the target first and the slug typed must match the tenant being emptied, so a
habitual `--execute` cannot wipe a tenant on its own.

## What clearing actually removes

Far more than the twelve tables the artifact carries. The clear set is the transitive closure of everything
that references those tables, which is 136 of 162 tables. It has to be: the artifact ships `ServiceLines`
rows on well-known Guids, so those rows must go, and anything pointing at them must go first or the DELETE
fails on a foreign key.

What survives is org structure, configuration, and reference data (`Organizations`, `ConfigSettings`,
`FieldDefinitions`, `Campuses`, `Departments`, `PmSchedules` and similar), because nothing there references
the artifact and the artifact could not put it back.

**Clearing removes every user.** The tenant is then unreachable through the normal login, and the admin API
is behind interactive Entra. Recovery is a single job run against the tenant, using the `ensureAdmin` mode of
the pre-prod seed job, which refuses to run if any user still exists. Plan for it; do not discover it.

## Known problems a smaller dataset does not fix

- **Checkpoints are written to a container the IaC never created** (lnicoara/cmms#2968). The blob store
  hash-suffixes every container name, so `load-checkpoints` becomes `load-checkpoints-<16 hex>`. Anyone
  watching the declared container sees zero forever. This once caused a working eight-hour load to be judged
  hung and killed.
- **Bulk-copy throughput is roughly nine minutes per 100,000-row chunk** on the pre-prod tier. The full
  dataset is 438 chunks, so it cannot finish inside the job's twelve-hour timeout. This is the reason the
  small dataset exists.
- **The clear can time out** on a table holding millions of rows, because the command timeout is not
  reachable from the deployed job. Note that a timed-out DELETE may still have committed server side.
- **Constraint trust is not re-established.** `SqlBulkCopyOptions.Default` checks no foreign keys and nothing
  re-trusts them afterward, so index rebuild, constraint re-trust, and statistics are operator work
  (lnicoara/cmms#2910). Relational validity is the artifact's property, which is what the generator's
  self-validation exists to guarantee.

## Schema compliance is a promotion concern, not a load-test one

A load moves rows into a database whose schema is already settled. Deciding what schema an environment runs,
and changing it, belong to promoting a build: `scripts/pre-prod/deploy-pre-prod.sh` applies migrations as
part of promoting a named commit, and that is the only thing that may.

The loader therefore never migrates anything and never asks whether the target is current. It asks one
question, and it is a narrower one: **do these rows fit these tables?** The artifact's `manifest.json`
records the migration ids its model carried, the target records what it has applied in
`__EFMigrationsHistory`, and the two compare directly.

- **Target behind the artifact.** The rows were written against migrations the database has never applied,
  so the load declines, names both versions, and changes nothing. The fix is to regenerate against the
  schema the target runs. Never to migrate the target.
- **Target ahead of the artifact.** Migrations are additive by convention, so the rows still fit. Reported,
  and the load continues; the load plan's column verification is the backstop.
- **Artifact with no recorded schema.** Anything generated before `lnicoara/cmms#2978` has no migration list.
  It still opens, and the report says plainly that the fit could not be checked.

This is deliberately narrower than what the loader used to do. It compared the target against **its own
binary's** model and told the operator to run the migration job, and `load-tenant.sh` duly grew a step that
built and ran `caj-cmms-migrate` against pre-prod on every invocation. A data load could migrate the
environment it was measuring, and every migration landing on `main` invalidated every artifact already
generated. `tests/infra/load-job-conformance.test.sh` now asserts there is no path back to it.

## Preflight refuses more often than it fails

The loader will not load into a database whose row counts disagree with the chunks it believes have landed.
A deficit always refuses. An excess is probed, and chunks provably present are adopted rather than reloaded.

None of that is a bug to work around. Every one of those refusals exists because the alternative is a
plausible-looking wrong dataset.
