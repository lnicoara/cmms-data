# Loading a dataset

Loading happens from the `cmms` repository, not this one. This page records what the loader assumes about a
dataset in `data/`, and the known problems that are not fixed by using a smaller one.

## The command

Loading happens from THIS repository now (lnicoara/cmms#3026); it used to happen from a `cmms` checkout.

```bash
# logged in to the target subscription
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small

# the dry run: stages the artifact, verifies it, writes nothing
scripts/pre-prod/load-pre-prod.sh --artifact-dir=$HOME/git/cmms-data/data/small --plan
```

It **loads**. Typing the name is the intent, so there is no second flag confirming it, and `--plan` is the
opt-out. `--execute` is still accepted and does nothing new, because every existing note and shell history
says it.

**It deletes nothing** (lnicoara/cmms#3025). The `--clear-target` flag documented here previously emptied
the target's whole foreign-key closure before loading, with each DELETE committing on its own, so a load
that failed afterwards left the tenant holding less than it started with. The flag is gone and the script
refuses it with an explanation. The collision it existed for was the generator claiming ids and codes the
target already owned, and that is fixed at the source instead.

The loader loads **on top of** whatever is already in the target. Rows it did not write are reported and
kept: for a load test they are more rows under load, which is the quantity being measured.

## Why clearing is gone

The load path can no longer delete, and this section records what it used to do so the reasoning is not
lost with the code.

`--clear-target` did not empty the tables the artifact carried. It emptied the transitive closure of
everything referencing them, which under lnicoara/cmms#2993 (every table generated) is the whole tenant
database. It ran BEFORE the load, and its DELETEs committed one table at a time with no encompassing
transaction, so any load that failed afterwards left the target holding less than it started with.
Repeated attempts at one load hit that hardest, which is exactly when a load is being debugged.

It existed because the artifact collided with the target: it emitted `ServiceLines` at `ServiceLineSeed`'s
Guids and at the codes `CE` and `FE`, hitting both the primary key and the unique `IX_ServiceLines_Code`.
One of those Guids is inserted by a **migration** into every tenant database, so a brand-new unseeded
tenant collided too, and this tooling's standing advice to "load into a dedicated unseeded tenant" was
wrong. Both values are seed-derived now, so the artifact claims nothing the schema or the seeder owns, and
there is nothing to clear.

Deleting a tenant's contents is still a legitimate act; it is simply not something a script named for
loading does on the way past. `TargetCleaner` remains, with its own tests, for a caller that means it.

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
binary's** model and told the operator to run the migration job, and the load script duly grew a step that
built and ran `caj-cmms-migrate` against pre-prod on every invocation. A data load could migrate the
environment it was measuring, and every migration landing on `main` invalidated every artifact already
generated. `tests/infra/load-job-conformance.test.sh` now asserts there is no path back to it.

## Preflight refuses more often than it fails

The loader will not load into a database whose row counts disagree with the chunks it believes have landed.
A deficit always refuses. An excess is probed, and chunks provably present are adopted rather than reloaded.

None of that is a bug to work around. Every one of those refusals exists because the alternative is a
plausible-looking wrong dataset.
