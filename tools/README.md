# The load-test tooling

This is the source. Edit it here.

Until lnicoara/cmms#3026 this directory was a **read-only snapshot** copied from `cmms`, under a rule
saying never to edit it, because the projects could not build here: both carried a `ProjectReference` to
`../../src/Cmms.Infrastructure`, a path this repository does not have. That rule is reversed.

The snapshot was not a neutral arrangement. Measured against what pre-prod actually ran, the operator
script was hundreds of lines adrift, the conformance suite was behind, and the job template was behind,
while the C# was in sync. A copy kept as documentation rots fastest on the file most able to mislead
somebody, and it still described a flag that emptied the tenant and no longer exists.

## How the coupling is handled now

It is not removed. It is **versioned**.

`Cmms.Infrastructure` is a `PackageReference` pinned by `<CmmsVersion>` in
[Directory.Build.props](../Directory.Build.props). `cmms` publishes itself as `1.0.0-g<shortsha>`, one
version per commit, so this repository references a build rather than a checkout.

A source copy would not have worked, and the reason is worth stating precisely. `ModelBridge` builds a real
`CmmsDbContext` over a never-opened connection for **two** reasons, and only the first is metadata:

1. the artifact's column manifest is derived from the live EF model rather than hand-listed, so a migration
   cannot silently invalidate it
2. it yields a real `EntityEntry`, which is what lets generated audit rows run through the product's own
   `AuditDiff` and `AuditEventFactory` (extracted for exactly this in `lnicoara/cmms#2903`) instead of a
   second implementation that would drift

A model-snapshot file satisfies (1) and not (2). A fork satisfies neither: it would make the generator
agree with itself and disagree with the database, and that surfaces as a corrupt bulk load rather than as a
build error.

## What is here

| project | what it does |
|---|---|
| `Cmms.LoadDataGenerator` | writes the artifact. Opens no database, reads no Azure config, runs on a laptop |
| `Cmms.LoadDataRunner` | bulk-copies one artifact into one provisioned tenant database, in-VNet as a job |

The runner keeps a `ProjectReference` on the generator, deliberately. It is local, and it means the loader
writes against the same `ModelBridge` derivation the generator wrote with, so the two cannot drift on a
migration (`lnicoara/cmms#2908` grill round 5). Making that a package would reintroduce the drift it exists
to prevent, between two things that always ship together.

`Cmms.MigrationRunner` and `Cmms.TenantSeedRunner` used to be snapshotted here and are gone. They are the
deploy path, not load testing, and they live in `cmms`.

## Building

Needs `Cmms.*` in `../.packages/`, packed out of a cmms checkout at the pinned commit.
`scripts/pre-prod/load-pre-prod.sh` does that automatically before it builds the image; by hand:

```bash
dotnet pack <cmms>/src/Cmms.Infrastructure -c Release -p:Version=1.0.0-g<pin> -o ../.packages
dotnet build ../Cmms.Data.sln -c Release
```

A local folder rather than a hosted feed, because the image builds remotely and a feed would need a
credential to cross into that build without landing in a layer. This needs none, and it cannot go stale:
the packages are produced from your checkout seconds before they are consumed.

`nuget.config` maps `Cmms.*` to that folder and everything else to nuget.org, so a public package taking
one of these names cannot answer first for tooling that writes directly into a production-shaped
database.
