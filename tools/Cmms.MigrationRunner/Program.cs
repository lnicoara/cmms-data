// lnicoara/cmms#33: out-of-band EF migration fan-out runner.
//
// Applies pending migrations to the shared catalog database and every ACTIVE tenant database
// (database-per-tenant, ADR-01). It is the controlled alternative to app-startup migration:
// a pre-flight pass connects to every target and reads its pending migrations without writing;
// only if all targets are reachable does it apply (catalog first, then tenants by slug),
// continuing past a per-database failure so the report covers the whole fleet. Exit code is
// non-zero if any database failed, so a deploy pipeline can refuse to roll code forward until
// the fan-out is fully green. `--dry-run` stops after the pre-flight and writes nothing.
//
// Cross-database all-or-nothing is impossible; safety comes from additive migrations run before
// the code deploy, not from this runner. New-tenant provisioning (SqlServerTenantProvisioner) is
// untouched.

using Cmms.Application.Abstractions;
using Cmms.Domain.Tenancy;
using Cmms.Infrastructure;
using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

// Keep the bare --dry-run flag out of the host's command-line configuration, which parses --key value
// pairs and would otherwise treat the switch as a key awaiting a value.
var hostArgs = args.Where(a => !string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
builder.Services.AddCmmsInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();

using var scope = host.Services.CreateScope();
var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
var secrets = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

var catalogConn = config.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException("ConnectionStrings:Catalog is required to reach the control-plane catalog.");

// The catalog is both the source of the tenant list and a migration target. If it is unreachable we
// cannot even enumerate tenants, so fail fast with a clear message before building any targets.
List<string> activeSlugs;
try
{
    await using var catalog = OpenCatalog(catalogConn);
    activeSlugs = await catalog.Tenants.AsNoTracking()
        .Where(t => t.Status == TenantStatus.Active)
        .OrderBy(t => t.Slug)
        .Select(t => t.Slug)
        .ToListAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot reach the catalog database to enumerate tenants: {ex.Message}");
    return 1;
}

// Build the target list: catalog first (control plane), then active tenants by slug. A tenant whose
// connection secret cannot be resolved is recorded as an unreachable target rather than aborting the list.
var targets = new List<Target> { new("catalog", catalogConn, IsCatalog: true, SecretError: null) };
foreach (var slug in activeSlugs)
{
    string conn;
    try
    {
        conn = await secrets.GetSecretAsync(Tenant.SecretNameFor(slug));
    }
    catch (Exception ex)
    {
        targets.Add(new Target(slug, Conn: null, IsCatalog: false, SecretError: ex.Message));
        continue;
    }

    // Safety guard: a stale or miswired secret must never point a tenant's migrations at the catalog
    // or another tenant's database (that would apply the wrong model and corrupt it). The provisioner
    // names every tenant database cmms-tenant-{slug}; reject any connection that targets a different
    // database before MigrateAsync ever runs.
    var expectedDb = $"cmms-tenant-{slug}";
    string actualDb;
    try
    {
        actualDb = new SqlConnectionStringBuilder(conn).InitialCatalog;
    }
    catch (Exception ex)
    {
        targets.Add(new Target(slug, Conn: null, IsCatalog: false, SecretError: $"unparsable connection string: {ex.Message}"));
        continue;
    }
    if (!string.Equals(actualDb, expectedDb, StringComparison.OrdinalIgnoreCase))
    {
        targets.Add(new Target(slug, Conn: null, IsCatalog: false,
            SecretError: $"connection targets database '{actualDb}', expected '{expectedDb}'"));
        continue;
    }

    targets.Add(new Target(slug, conn, IsCatalog: false, SecretError: null));
}

Console.WriteLine($"cmms migration fan-out{(dryRun ? " (dry-run)" : "")}: catalog + {targets.Count - 1} active tenant database(s)");

// --- Pre-flight: connect to every target and read pending migrations (no writes). ---
var results = new List<Result>();
foreach (var target in targets)
{
    if (target.SecretError is not null)
    {
        results.Add(new Result(target.Name, Reachable: false, Pending: [], Applied: false, Error: $"secret: {target.SecretError}"));
        continue;
    }

    try
    {
        await using var ctx = Open(target);
        var pending = (await ctx.Database.GetPendingMigrationsAsync()).ToArray();
        results.Add(new Result(target.Name, Reachable: true, pending, Applied: false, Error: null));
    }
    catch (Exception ex)
    {
        results.Add(new Result(target.Name, Reachable: false, Pending: [], Applied: false, Error: ex.Message));
    }
}

var unreachable = results.Count(r => !r.Reachable);

// --- Apply: only when every target is reachable and this is not a dry run. ---
if (!dryRun && unreachable == 0)
{
    for (var i = 0; i < targets.Count; i++)
    {
        if (results[i].Pending.Length == 0) continue; // already current
        try
        {
            await using var ctx = Open(targets[i]);
            await ctx.Database.MigrateAsync();
            results[i] = results[i] with { Applied = true };
        }
        catch (Exception ex)
        {
            results[i] = results[i] with { Error = ex.Message };
        }
    }
}
else if (!dryRun)
{
    Console.WriteLine($"ABORTED before applying: {unreachable} target(s) unreachable. No database was migrated.");
}

// --- Report. ---
Console.WriteLine();
foreach (var r in results)
{
    var status = !r.Reachable ? "UNREACHABLE"
        : r.Error is not null ? "FAILED"
        : r.Pending.Length == 0 ? "up-to-date"
        : dryRun ? $"{r.Pending.Length} pending"
        : r.Applied ? $"applied {r.Pending.Length}"
        : "pending (not applied)";
    Console.WriteLine($"  {r.Name,-24} {status}");
    if (r.Pending.Length > 0)
        Console.WriteLine($"      pending: {string.Join(", ", r.Pending)}");
    if (r.Error is not null)
        Console.WriteLine($"      error: {r.Error}");
}

var failed = results.Count(r => !r.Reachable || r.Error is not null);
Console.WriteLine();
Console.WriteLine($"done: {results.Count} target(s), {failed} failed.");
return failed == 0 ? 0 : 1;

// --- Helpers ---
DbContext Open(Target t) => t.IsCatalog ? OpenCatalog(t.Conn!) : OpenTenant(t.Conn!);

static CatalogDbContext OpenCatalog(string conn) =>
    // Resume-tolerant: the migration runner is often the first DB touch in a deploy, when a serverless
    // DB may be paused. Ride out the cold start instead of failing preflight/migration. lnicoara/cmms#558
    new(new DbContextOptionsBuilder<CatalogDbContext>().UseResilientSqlServer(conn).Options);

// The runner constructs the per-tenant context directly with the resolved connection string,
// bypassing the request-scoped ITenantContext (mirroring the design-time factory). Migrations are
// DDL, so the audit interceptor is intentionally not attached.
static CmmsDbContext OpenTenant(string conn) =>
    // Resume-tolerant for the same reason as the catalog above: a paused tenant DB must resume into a
    // slow migration, not a failed one. lnicoara/cmms#558
    new(new DbContextOptionsBuilder<CmmsDbContext>().UseResilientSqlServer(conn).Options, ServiceLineContext.None);

internal sealed record Target(string Name, string? Conn, bool IsCatalog, string? SecretError);
internal sealed record Result(string Name, bool Reachable, string[] Pending, bool Applied, string? Error);
