using Cmms.Application.Abstractions;
using Cmms.Domain.Tenancy;
using Cmms.Infrastructure;
using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Tenancy;
using Cmms.LoadDataGenerator;
using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// lnicoara/cmms#2908, per epic #2731: bulk-loads the offline artifact from #2876 into ONE provisioned
// tenant database, as a Container Apps Job inside the VNet so it can reach a private data tier.
//
// PLAN ONLY unless --execute is passed. The preflight guards below are copied from the tenant seed runner
// (#2442) rather than reinvented, because the failure they prevent (a same-slug secret miswired to another
// environment) is one the database-name check alone does not catch.

// --- Artifact-only verification, BEFORE anything else exists. ---
// It runs above the host builder deliberately. AddCmmsInfrastructure demands ConnectionStrings:Catalog and
// throws without it, so ANY code below this line is unreachable on a machine that has no database
// credentials. That is exactly the machine an operator verifies an artifact on, and putting this check
// below the host made it impossible to run: it died on a missing connection string while being asked a
// question about a directory of files.
if (args.Any(a => string.Equals(a, "--verify-artifact", StringComparison.OrdinalIgnoreCase)))
{
    var artifactDir = args
        .Where(a => a.StartsWith("--artifact=", StringComparison.OrdinalIgnoreCase))
        .Select(a => a["--artifact=".Length..])
        .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(artifactDir))
    {
        Console.Error.WriteLine("--verify-artifact needs --artifact=<dir>.");
        return 1;
    }
    try
    {
        var a = Artifact.Open(artifactDir);
        using var m = new ModelBridge();
        var p = LoadPlan.Build(a, m);
        Console.WriteLine($"artifact {a.Directory}");
        Console.WriteLine($"  format {a.FormatVersion}  seed {a.Seed}  hash {a.Hash}");
        foreach (var line in p.Describe()) Console.WriteLine(line);
        Console.WriteLine();
        Console.WriteLine("artifact verified against this build's model. No database was contacted.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Artifact verification failed: {ex.Message}");
        return 1;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCmmsInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();

using var scope = host.Services.CreateScope();
var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
var secrets = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

// Checkpoints go to the LOAD-TEST account, addressed by their own key, NOT through the DI-registered
// IBlobStore. That one binds to Storage:BlobUrl, which in every deployed environment is the work-order
// PHOTO store holding real uploaded product data. A bulk load job has no business writing there, and
// overriding a shared key so it means two different accounts depending on who reads it is worse than
// giving this workload its own. Same reasoning that gave the Compliance Matrix snapshots
// Storage:ComplianceBlobUrl (#2208). lnicoara/cmms#2917 grill round 2.
var checkpointBlobUrl = config["Storage:LoadCheckpointBlobUrl"];
IBlobStore blobs;
if (!string.IsNullOrWhiteSpace(checkpointBlobUrl))
{
    blobs = new Cmms.Infrastructure.Storage.AzureBlobStore(
        checkpointBlobUrl, config["Storage:ManagedIdentityClientId"]);
}
else if (builder.Environment.IsDevelopment())
{
    // Local runs against a container get the dev file store, so a load can be exercised end to end
    // with no Azure at all.
    blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
}
else
{
    Console.Error.WriteLine(
        "Storage:LoadCheckpointBlobUrl is required outside Development. Without it the loader has nowhere " +
        "durable to record which chunks landed, so an interrupted load could not resume and would have to " +
        "be reloaded from the start. Refusing rather than falling back to the product's photo store.");
    return 1;
}

var options = new LoaderOptions();
config.GetSection("Load").Bind(options);
var parseError = CommandLine.Parse(args, options);
if (parseError is not null)
{
    Console.Error.WriteLine(parseError);
    return 1;
}

if (string.IsNullOrWhiteSpace(options.Artifact))
{
    Console.Error.WriteLine("--artifact=<dir> is required (the directory holding manifest.json).");
    return 1;
}
var slug = options.Tenant.Trim().ToLowerInvariant();
if (string.IsNullOrEmpty(slug))
{
    Console.Error.WriteLine("--tenant=<slug> is required (the tenant to load, for example 'loadtest').");
    return 1;
}

// --- Guard: a clear must NAME the tenant it empties, and the name must match the target. ---
// This lives in the binary rather than only in the operator script because the script is not the only way
// to start this job. A Bicep deploy, a portal edit, or a change to the job's environment all reach the
// container directly, and a boolean flag would let any of them empty whatever tenant the slug happened to
// point at. Two independent values have to agree HERE, at the thing that performs the delete.
if (!string.IsNullOrWhiteSpace(options.ClearTargetSlug) &&
    !string.Equals(options.ClearTargetSlug.Trim(), slug, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"--clear-target names '{options.ClearTargetSlug.Trim()}' but the target tenant is '{slug}'. " +
        "Clearing deletes every row the artifact owns, so it must name the tenant it is about to empty. " +
        "Refusing.");
    return 1;
}

var catalogConn = config.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException("ConnectionStrings:Catalog is required to reach the control-plane catalog.");

// --- Guard: the tenant exists and is Active in the catalog. (#2442) ---
TenantStatus? status;
try
{
    await using var catalog = new CatalogDbContext(
        new DbContextOptionsBuilder<CatalogDbContext>().UseResilientSqlServer(catalogConn).Options);
    status = await catalog.Tenants.AsNoTracking()
        .Where(t => t.Slug == slug)
        .Select(t => (TenantStatus?)t.Status)
        .FirstOrDefaultAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot reach the catalog to look up tenant '{slug}': {ex.Message}");
    return 1;
}
if (status is null)
{
    Console.Error.WriteLine($"Tenant '{slug}' is not in the catalog. Provision it first (POST /api/admin/organizations).");
    return 1;
}
if (status != TenantStatus.Active)
{
    Console.Error.WriteLine($"Tenant '{slug}' is {status}, not Active. Refusing to load.");
    return 1;
}

// --- Guard: the secret resolves, targets the tenant's OWN database, on THIS environment's server. (#2442) ---
string conn;
try
{
    conn = await secrets.GetSecretAsync(Tenant.SecretNameFor(slug));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot resolve the connection secret for '{slug}': {ex.Message}");
    return 1;
}

SqlConnectionStringBuilder tenantCsb;
try
{
    tenantCsb = new SqlConnectionStringBuilder(conn);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unparsable connection string for '{slug}': {ex.Message}");
    return 1;
}

var expectedDb = $"cmms-tenant-{slug}";
if (!string.Equals(tenantCsb.InitialCatalog, expectedDb, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"Connection for '{slug}' targets database '{tenantCsb.InitialCatalog}', expected '{expectedDb}'. Refusing to load.");
    return 1;
}
var expectedServer = new SqlConnectionStringBuilder(catalogConn).DataSource;
if (!string.Equals(tenantCsb.DataSource, expectedServer, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"Connection for '{slug}' targets server '{tenantCsb.DataSource}', expected this environment's " +
        $"'{expectedServer}'. Refusing to load.");
    return 1;
}

// --- Guard: migrations are current. Loading against a stale schema writes rows the model cannot read. ---
try
{
    await using var db = new CmmsDbContext(
        new DbContextOptionsBuilder<CmmsDbContext>().UseResilientSqlServer(conn).Options, ServiceLineContext.None);
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
    if (pending.Length > 0)
    {
        Console.Error.WriteLine(
            $"Tenant '{slug}' has {pending.Length} pending migration(s); run the migration job first. Refusing to load.");
        return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot check migrations for '{slug}': {ex.Message}");
    return 1;
}

// --- Plan, then load only if told to. ---
try
{
    var artifact = Artifact.Open(options.Artifact);
    using var model = new ModelBridge();
    var plan = LoadPlan.Build(artifact, model);

    var target = new TargetDatabase(conn, options.TimeoutSeconds);

    // --- Optional, separately flagged, destructive: empty the artifact's write set. ---
    //
    // The loader itself never deletes (lnicoara/cmms#2908 grill round 3) and that still holds: this runs
    // BEFORE the loader, is a distinct operation, and needs its own flag on top of --execute. It exists
    // because the chosen pre-prod target is the already-seeded demo-health tenant, whose ServiceLines
    // rows carry the same well-known Guids the artifact ships, so a load would collide on the primary key.
    //
    // It lives here rather than in a .sql file for one reason worth stating: everything above this line is
    // the guard set (tenant in the catalog, Active, secret targeting its OWN database on THIS environment's
    // server, migrations current). A DELETE has more need of those guards than a load does, and
    // reimplementing them alongside a script would be how they drift.
    if (!string.IsNullOrWhiteSpace(options.ClearTargetSlug))
    {
        Console.WriteLine();
        // ClearAsync prints as it deletes, so the caller does not re-print clean.Lines. A partial clear
        // has to be visible at the moment it happens, not assembled after a run that may never return.
        await TargetCleaner.ClearAsync(plan, model, target, options.Execute, CancellationToken.None);
    }

    var checkpoints = new BlobCheckpointStore(
        blobs, options.CheckpointContainer, artifact.Hash, target.DatabaseName);

    var report = await new Loader(artifact, plan, target, checkpoints, options).RunAsync(CancellationToken.None);
    foreach (var line in report.Lines) Console.WriteLine(line);
    return report.Ok ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Load failed: {ex.Message}");
    return 1;
}
