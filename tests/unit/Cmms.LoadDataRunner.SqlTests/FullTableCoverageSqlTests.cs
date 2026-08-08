using Cmms.LoadDataGenerator;
using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cmms.LoadDataRunner.SqlTests;

// lnicoara/cmms#2993: the artifact covers every tenant table, and the whole of it loads.
//
// The defect these pin: the generator emitted 12 of 162 tables, and nothing counted the other 150. The
// artifact validated, the load reported success, and the first signal was an operator opening Preventive
// Maintenance, Procedures, Inventory, Contracts and Purchasing in pre-prod and finding them empty. Because
// --clear-target empties the artifact's whole foreign-key closure first, the load left the tenant holding
// LESS than before it ran.
//
// Structural validation is not enough on its own here, and that distinction is the reason these tests go
// through a real SQL Server. A synthesized row can satisfy every check the generator can make about itself
// and still be rejected by the database: a NOT NULL column the model marks optional, a check constraint the
// generator does not know about, a foreign key whose parent was emitted in the wrong order. Those only
// surface against a real schema, which in production means inside a container job after a multi-gigabyte
// upload. Here it means a test.
[Trait("Category", "SqlServer")]
[Collection("SqlSerial")]   // lnicoara/cmms#2148: only one SQL Edge container at a time
public sealed class FullTableCoverageSqlTests : IClassFixture<LoadSqlEdgeFixture>, IAsyncLifetime, IDisposable
{
    private readonly LoadSqlEdgeFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coverage-" + Guid.NewGuid().ToString("N"));

    public FullTableCoverageSqlTests(LoadSqlEdgeFixture fx) => _fx = fx;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Empties the database this suite filled.
    ///
    /// The fixture hands one database to the whole class and the SqlSerial collection runs suites one after
    /// another against it, so a suite that loads 158 tables and walks away leaves every later suite reading
    /// somebody else's rows. That is not hypothetical: it broke ClearPreservesAdminLoginSqlTests, whose
    /// setup does a bare DELETE FROM [Users], the moment Users acquired populated children. Children before
    /// parents, in the model's own dependency order, which is the only ordering the foreign keys accept.
    /// </summary>
    public async Task DisposeAsync()
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

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Conn()
    {
        using var db = _fx.NewContext();
        return db.Database.GetConnectionString()!;
    }

    /// <summary>A small artifact that still exercises every table, which is the point.</summary>
    private string BuildArtifact()
    {
        Directory.CreateDirectory(_dir);
        var options = new GeneratorOptions
        {
            Seed = "coverage-test", OutDir = _dir,
            WorkOrders = 40, Assets = 20, Users = 5,
            Campuses = 1, BuildingsPerCampus = 1, FloorsPerBuilding = 1, RoomsPerFloor = 2,
            Manufacturers = 2, ModelsPerManufacturer = 1, AssetTypes = 3, Accounts = 2,
            RowsPerChunk = 25, SynthesizedRowsPerTable = 3,
        };
        var stats = new Generator(options).Run();
        ArtifactManifest.Write(_dir, options, stats.TableTotals);
        return _dir;
    }

    /// <summary>
    /// Every table in the model is either written by the artifact or exempt with a reason. No third state.
    ///
    /// This is the assertion whose absence let the gap exist: the old check enumerated a hand-written list
    /// of 12 expected tables, so it could only ever confirm the 12 it already knew about. Reading the model
    /// means a table added by a migration is expected the moment it exists.
    /// </summary>
    [SkippableFact]
    public void Every_model_table_is_generated_or_exempt_with_a_reason()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact();
        var artifact = Artifact.Open(dir);
        using var bridge = new ModelBridge();

        var covered = artifact.Tables.ToHashSet(StringComparer.Ordinal);
        var unclassified = TableCoverage.Unclassified(bridge.AllTableNames(), covered);

        Assert.True(unclassified.Count == 0,
            "these model tables are neither generated nor exempt: " + string.Join(", ", unclassified));
        Assert.Empty(TableCoverage.Pending);
        Assert.All(TableCoverage.Exempt.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    /// <summary>
    /// The artifact loads into a real schema, all of it.
    ///
    /// Asserted per table rather than on a total, because a total hides the case that matters: 157 tables
    /// landing and one silently contributing zero still produces a plausible-looking sum.
    /// </summary>
    [SkippableFact]
    public async Task Every_generated_table_lands_in_the_database()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact();
        var artifact = Artifact.Open(dir);
        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var target = new TargetDatabase(Conn(), timeoutSeconds: 180);

        await TargetCleaner.ClearAsync(plan, model, target, execute: true, CancellationToken.None);
        var report = await new Loader(
            artifact, plan, target, new NoCheckpoints(),
            new LoaderOptions { Artifact = dir, Tenant = "t", Execute = true, TimeoutSeconds = 180 })
            .RunAsync(CancellationToken.None);

        Assert.True(report.Ok, string.Join("\n", report.Lines));

        var empty = new List<string>();
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        foreach (var table in artifact.Tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT_BIG(*) FROM [{table}]";
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 0) empty.Add(table);
        }

        Assert.True(empty.Count == 0,
            $"{empty.Count} of {artifact.Tables.Count} tables loaded zero rows: " + string.Join(", ", empty));
    }

    /// <summary>
    /// The count is not a hand-written number that drifts. If a migration adds a table, coverage has to
    /// follow it, and this is what fails when it does not.
    /// </summary>
    [SkippableFact]
    public void Coverage_tracks_the_model_rather_than_a_written_down_count()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact();
        var artifact = Artifact.Open(dir);
        using var bridge = new ModelBridge();

        var expected = bridge.AllTableNames().Count - TableCoverage.Exempt.Count;
        Assert.Equal(expected, artifact.Tables.Count);
    }

    private sealed class NoCheckpoints : ICheckpointStore
    {
        public Task<bool> IsCompleteAsync(string table, int chunk, CancellationToken ct) => Task.FromResult(false);
        public Task MarkCompleteAsync(string table, int chunk, string detail, CancellationToken ct) => Task.CompletedTask;
    }
}
