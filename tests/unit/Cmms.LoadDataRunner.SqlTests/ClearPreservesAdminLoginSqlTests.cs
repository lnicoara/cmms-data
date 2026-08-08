using Cmms.Domain.Identity;
using Cmms.Infrastructure.Identity;
using Cmms.LoadDataGenerator;
using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cmms.LoadDataRunner.SqlTests;

// lnicoara/cmms#2979: --clear-target must not end with a tenant nobody can log into.
//
// What happened in pre-prod: the clear emptied Users like any other table, the artifact refilled it with
// 500 synthetic accounts whose PasswordHash is a placeholder that matches no password (Generator.cs:423),
// and the run reported success. The operator was locked out of the environment being load-tested, and
// getting back in took a platform-admin Entra token and a hand-written API call. The purpose-built repair
// job could not help either, because it refuses when ANY user row exists.
//
// The assertion that was missing is the one below, and it is deliberately about the OUTCOME rather than
// about the mechanism: not "Users was skipped" but "a real password still verifies afterwards". Every
// cheaper check passed while the system was unusable, which is exactly the failure this pins.
[Trait("Category", "SqlServer")]
[Collection("SqlSerial")]   // lnicoara/cmms#2148: only one SQL Edge container at a time
public sealed class ClearPreservesAdminLoginSqlTests : IClassFixture<LoadSqlEdgeFixture>, IAsyncLifetime, IDisposable
{
    private readonly LoadSqlEdgeFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clearadmin-" + Guid.NewGuid().ToString("N"));

    public ClearPreservesAdminLoginSqlTests(LoadSqlEdgeFixture fx) => _fx = fx;

    // The fixture hands the whole CLASS one database, and both tests here create a user named 'admin',
    // which is unique-indexed. Without this the second test collides with the first one's row, and a run
    // after any other suite that left users behind collides before it starts. Children before parents.
    // The fixture hands the whole CLASS one database, and both tests here create a user named 'admin',
    // which is unique-indexed, so residue from a sibling suite collides before this one starts.
    //
    // Emptied to a FIXPOINT, in the model's own order, rather than by naming Users and AccessGroups. Those
    // two acquired populated children the moment the generator started covering all 158 tables, and a bare
    // DELETE FROM [Users] then fails on FK_PoApprovalRules_AccessGroups_ApproverAccessGroupId. Naming
    // tables here would be a second copy of the schema that nothing keeps honest. lnicoara/cmms#2993.
    public async Task InitializeAsync()
    {
        if (!_fx.Available) return;
        using var model = new ModelBridge();
        var order = LoadPlan.ClearOrder(model.AllTableNames().ToList(), model);

        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();

        var pending = Enumerable.Reverse(order).ToList();
        for (var pass = 0; pending.Count > 0 && pass < order.Count + 2; pass++)
        {
            var deferred = new List<string>();
            var progressed = false;
            foreach (var table in pending)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 120;
                cmd.CommandText = $"DELETE FROM [{table}]";
                try { await cmd.ExecuteNonQueryAsync(); progressed = true; }
                catch (SqlException ex) when (ex.Number == 547) { deferred.Add(table); }
            }
            if (!progressed) break;
            pending = deferred;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Conn()
    {
        using var db = _fx.NewContext();
        return db.Database.GetConnectionString()!;
    }

    private string BuildArtifact()
    {
        Directory.CreateDirectory(_dir);
        var options = new GeneratorOptions
        {
            Seed = "clear-admin-test", OutDir = _dir,
            WorkOrders = 40, Assets = 20, Users = 5,
            Campuses = 1, BuildingsPerCampus = 1, FloorsPerBuilding = 1, RoomsPerFloor = 2,
            Manufacturers = 2, ModelsPerManufacturer = 1, AssetTypes = 3, Accounts = 2,
            RowsPerChunk = 25,
        };
        var stats = new Generator(options).Run();
        ArtifactManifest.Write(_dir, options, stats.TableTotals);
        return _dir;
    }

    /// <summary>
    /// The whole point. An admin exists with a password a person actually knows; a clear plus load runs;
    /// that same password must still verify against the stored hash afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_clear_and_load_leaves_the_admin_able_to_log_in()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        const string Password = "Harpeth-Restore-4471";
        var hasher = new Pbkdf2PasswordHasher();

        // An admin shaped like a real one: it carries an access group, which is what grants its rights, and
        // that group is inside the artifact's write set and would otherwise be deleted underneath it.
        var groupId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await using (var db = _fx.NewContext())
        {
            db.AccessGroups.Add(new AccessGroup { Id = groupId, Name = "System Admin" });
            db.Users.Add(new User
            {
                Id = adminId,
                Username = "admin",
                DisplayName = "Administrator",
                PasswordHash = hasher.Hash(Password),
                IsActive = true,
                DefaultAccessGroupId = groupId,
            });
            await db.SaveChangesAsync();
        }

        var dir = BuildArtifact();
        var artifact = Artifact.Open(dir);
        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);

        var clean = await TargetCleaner.ClearAsync(plan, model, target, execute: true, CancellationToken.None);
        var report = await new Loader(
            artifact, plan, target, new InMemoryCheckpoints(),
            new LoaderOptions { Artifact = dir, Tenant = "t", Execute = true, TimeoutSeconds = 120 })
            .RunAsync(CancellationToken.None);
        Assert.True(report.Ok, string.Join("\n", report.Lines));
        Assert.Equal(1, await TargetCleaner.RestorePreservedAsync(target, clean, CancellationToken.None));

        // The assertion. Read the row back and verify the ORIGINAL password against the STORED hash, which
        // is what the login endpoint does. Asserting the row merely exists would pass against a row whose
        // hash had been overwritten by the artifact's placeholder.
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [PasswordHash], [DefaultAccessGroupId], [IsActive] FROM [Users] WHERE [Username] = 'admin'";
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "the admin user was deleted by the clear");
        Assert.True(hasher.Verify(Password, reader.GetString(0)),
            "the admin's password no longer verifies, so the tenant is unreachable");
        Assert.False(await reader.IsDBNullAsync(1),
            "the admin lost its access group, so it can log in but can do nothing");
        Assert.Equal(groupId, reader.GetGuid(1));
        Assert.True(reader.GetBoolean(2), "the admin was deactivated");
    }

    /// <summary>
    /// Preserving the admin must not preserve anything else. The synthetic users the artifact brings are
    /// exactly what a second load has to be able to replace, so leaving them behind would trade a lockout
    /// for a primary key collision on the next run.
    /// </summary>
    [SkippableFact]
    public async Task A_clear_still_removes_every_user_it_is_not_preserving()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var hasher = new Pbkdf2PasswordHasher();
        await using (var db = _fx.NewContext())
        {
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Username = "admin",
                PasswordHash = hasher.Hash("Harpeth-Restore-4471"), IsActive = true,
            });
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Username = "tech0000",
                PasswordHash = hasher.Hash("something-else-entirely"), IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var dir = BuildArtifact();
        var artifact = Artifact.Open(dir);
        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);

        var clean = await TargetCleaner.ClearAsync(plan, model, target, execute: true, CancellationToken.None);
        await TargetCleaner.RestorePreservedAsync(target, clean, CancellationToken.None);

        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [Username] FROM [Users]";
        await using var reader = await cmd.ExecuteReaderAsync();
        var left = new List<string>();
        while (await reader.ReadAsync()) left.Add(reader.GetString(0));

        Assert.Equal(new[] { "admin" }, left);
    }

    /// <summary>Checkpoints in memory: this suite is about the clear, not about resume.</summary>
    private sealed class InMemoryCheckpoints : ICheckpointStore
    {
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);

        public Task<bool> IsCompleteAsync(string table, int chunk, CancellationToken ct) =>
            Task.FromResult(_done.Contains($"{table}/{chunk}"));

        public Task MarkCompleteAsync(string table, int chunk, string detail, CancellationToken ct)
        {
            _done.Add($"{table}/{chunk}");
            return Task.CompletedTask;
        }
    }
}
