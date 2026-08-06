// lnicoara/cmms#2442, per #2435: out-of-band tenant demo-seed runner.
//
// Populates ONE already-provisioned tenant database with demo data (Cmms.Infrastructure DemoDataSeeder),
// run as a Container Apps Job inside the VNet so it can reach a private (public-access-disabled) pre-prod
// SQL server, the same reason the migration runner runs in-VNet rather than in CI. Provisioning stays in the
// audited admin API (POST /api/admin/organizations); this job only seeds an already-provisioned tenant.
// Preflight fails fast unless the tenant exists and is Active, its connection secret resolves to its OWN
// database, and its migrations are current. Idempotent: it no-ops if demo equipment already exists.

using Cmms.Application.Abstractions;
using Cmms.Domain.Tenancy;
using Cmms.Infrastructure;
using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Seeding;
using Cmms.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCmmsInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();

using var scope = host.Services.CreateScope();
var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
var secrets = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

var slug = (config["Seed:TenantSlug"] ?? string.Empty).Trim().ToLowerInvariant();
if (string.IsNullOrEmpty(slug))
{
    Console.Error.WriteLine("Seed:TenantSlug is required (the tenant to seed, for example 'demo-health').");
    return 1;
}

var catalogConn = config.GetConnectionString("Catalog")
    ?? throw new InvalidOperationException("ConnectionStrings:Catalog is required to reach the control-plane catalog.");

// --- Preflight: the tenant exists and is Active in the catalog. ---
TenantStatus? status;
try
{
    await using var catalog = OpenCatalog(catalogConn);
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
    Console.Error.WriteLine($"Tenant '{slug}' is {status}, not Active. Refusing to seed.");
    return 1;
}

// --- Resolve the tenant's own connection secret and guard that it targets the tenant's database. ---
// The same guard the migration runner uses: a stale or miswired secret must never point the seed at the
// catalog or another tenant's database.
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
var expectedDb = $"cmms-tenant-{slug}";
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
if (!string.Equals(tenantCsb.InitialCatalog, expectedDb, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Connection for '{slug}' targets database '{tenantCsb.InitialCatalog}', expected '{expectedDb}'. Refusing to seed.");
    return 1;
}
// Also require the tenant to live on THIS environment's SQL server (tenant databases sit on the same server
// as the catalog). This catches a same-slug secret miswired to another environment, which the database-name
// check alone would miss, so the seed can never land in the wrong environment. lnicoara/cmms#2442
var expectedServer = new SqlConnectionStringBuilder(catalogConn).DataSource;
if (!string.Equals(tenantCsb.DataSource, expectedServer, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Connection for '{slug}' targets server '{tenantCsb.DataSource}', expected this environment's '{expectedServer}'. Refusing to seed.");
    return 1;
}

// --- Preflight: migrations current; then seed (idempotent), mirroring the /seed-demo guard. ---
try
{
    await using var db = OpenTenant(conn);

    var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
    if (pending.Length > 0)
    {
        Console.Error.WriteLine($"Tenant '{slug}' has {pending.Length} pending migration(s); run the migration job first. Refusing to seed.");
        return 1;
    }

    // --- Recover a tenant left with NO members, which is not hypothetical. ---
    //
    // Clearing a tenant to load a synthetic artifact empties Users, AccessGroups and ServiceLines along with
    // everything else, and that locks the operator out of an environment whose only other door (the admin
    // API) is behind interactive Entra. The provisioner seeds a System Admin for exactly this case, but only
    // on the provisioning path, which a already-provisioned tenant never runs again.
    //
    // Deliberately NOT part of the demo seed: this restores ACCESS, not data, and it must be runnable against
    // a tenant that is meant to stay empty. It refuses if any user still exists, so it can never reset a
    // credential in use, matching SeedSystemAdminAsync's own guard.
    if (string.Equals(config["Seed:EnsureAdmin"], "true", StringComparison.OrdinalIgnoreCase))
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync())
        {
            Console.WriteLine($"Tenant '{slug}' already has at least one user. Refusing to touch credentials.");
            return 0;
        }

        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Seed:AdminPassword is required with Seed:EnsureAdmin.");
            return 1;
        }

        var username = string.IsNullOrWhiteSpace(config["Seed:AdminUsername"]) ? "admin" : config["Seed:AdminUsername"]!;
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        // The admin needs somewhere to stand: a service line to default into and a group to draw rights from.
        // Both are ordinary rows the clear removed, so recreate whichever is missing rather than assuming the
        // migration-seeded ones survived.
        var serviceLine = await db.ServiceLines.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Code == "MAIN");
        if (serviceLine is null)
        {
            serviceLine = new Cmms.Domain.Organization.ServiceLine { Name = "Main", Code = "MAIN" };
            db.ServiceLines.Add(serviceLine);
        }

        var adminGroup = await db.AccessGroups.IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.PermissionsJson == Cmms.Domain.Identity.SystemAdminGroup.PermissionsJson);
        if (adminGroup is null)
        {
            adminGroup = Cmms.Domain.Identity.SystemAdminGroup.New();
            db.AccessGroups.Add(adminGroup);
        }

        db.Users.Add(new Cmms.Domain.Identity.User
        {
            Username = username,
            DisplayName = "System Admin",
            PasswordHash = hasher.Hash(password),
            PasswordChangedAt = DateTimeOffset.UtcNow,
            // False on purpose. This is a recovery path for an operator who has just been locked out; forcing
            // a change dance on first sign-in adds a step to the one flow that exists to remove steps.
            MustChangePassword = false,
            IsActive = true,
            DefaultAccessGroupId = adminGroup.Id,
            DefaultServiceLineId = serviceLine.Id,
        });

        await db.SaveChangesAsync();
        Console.WriteLine($"Restored access to '{slug}': user '{username}', System Admin group, service line MAIN.");
        return 0;
    }

    if (await db.Assets.IgnoreQueryFilters().AnyAsync(a => a.AssetTag.StartsWith("CE-1") || a.AssetTag.StartsWith("FE-1")))
    {
        Console.WriteLine($"Tenant '{slug}' already has demo data. Nothing to do.");
        return 0;
    }

    // Seed atomically: the generator persists a service-line backfill before its final save, so without one
    // transaction a mid-seed failure could leave a half-populated tenant. The execution strategy is required
    // because the resilient (retrying) provider forbids a bare user transaction; a retried attempt rolls the
    // previous one back and re-runs the whole seed clean. lnicoara/cmms#2442
    DemoDataSeeder.DemoSeedResult r = null!;
    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        // Each attempt starts from a clean change tracker: a retried attempt must re-add the entities from
        // scratch, not double the ones a rolled-back prior attempt left tracked as Added.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        r = await DemoDataSeeder.SeedAsync(db, CancellationToken.None);
        await tx.CommitAsync();
    });

    Console.WriteLine(
        $"Seeded '{slug}': {r.Equipment} equipment, {r.Locations} locations, {r.Models} models, " +
        $"{r.Manufacturers} manufacturers, {r.Accounts} accounts, {r.Items} items.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Seeding '{slug}' failed: {ex.Message}");
    return 1;
}

// The runner constructs the contexts directly with the resolved connection, bypassing the request-scoped
// ITenantContext (mirroring the migration runner and the design-time factory). Resume-tolerant so a paused
// serverless database rides out its cold start instead of failing the seed. lnicoara/cmms#558
static CatalogDbContext OpenCatalog(string conn) =>
    new(new DbContextOptionsBuilder<CatalogDbContext>().UseResilientSqlServer(conn).Options);

static CmmsDbContext OpenTenant(string conn) =>
    new(new DbContextOptionsBuilder<CmmsDbContext>().UseResilientSqlServer(conn).Options, ServiceLineContext.None);
