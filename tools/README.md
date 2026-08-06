# Reference snapshot, not a working copy

The source under this directory is a **read-only snapshot** taken from `cmms` for reference. It does not
build here, and it must not be edited here.

## Why it does not build

Both projects carry a `ProjectReference` to `src/Cmms.Infrastructure` in the `cmms` repository, which in turn
pulls in `Cmms.Application` and `Cmms.Domain`:

```xml
<!-- Cmms.LoadDataGenerator.csproj -->
<ProjectReference Include="..\..\src\Cmms.Infrastructure\Cmms.Infrastructure.csproj" />

<!-- Cmms.LoadDataRunner.csproj -->
<ProjectReference Include="..\..\src\Cmms.Infrastructure\Cmms.Infrastructure.csproj" />
<ProjectReference Include="..\Cmms.LoadDataGenerator\Cmms.LoadDataGenerator.csproj" />
```

Those paths do not exist here, so a build fails immediately.

## Why that coupling is deliberate

`ModelBridge` constructs a real `CmmsDbContext` over the SQL Server provider with a connection string that is
never opened. EF resolves the full model, column names, types and keys, with no database present, so the
artifact's column manifest is **derived from the live EF model** rather than hand-listed. The loader reuses
the same `ModelBridge`, so the manifest the loader writes against is the identical derivation the generator
wrote with.

That is the whole referential-integrity story. A hand-listed manifest rots on the next migration silently,
and the failure surfaces as a corrupt bulk load much later rather than as a build error.

A forked copy of this source would reintroduce exactly that. It would compile against a snapshot of the
schema, agree with itself, and disagree with the database the moment a migration landed.

## What to do instead

Change the tools in `cmms`, regenerate, and commit the resulting data here alongside its profile. If you
need to know what produced a dataset in `data/`, read this snapshot. If you need to change what produces it,
go to `cmms`.

Snapshot taken at `cmms` commit 8db566e7.
