using System.IO.Compression;
using System.Text.Json;
using Cmms.LoadDataGenerator;
using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cmms.LoadDataRunner.SqlTests;

// lnicoara/cmms#2908: the load runner against a REAL migrated SQL Server database, no mocks.
//
// It generates a genuine artifact with the real generator, loads it with the real loader, and then reads
// the rows back out with raw SQL to check they survived the JSON-to-SQL round trip unchanged. The rest of
// the suite exercises the decisions the grill settled: plan-only writes nothing, a non-empty target is
// refused, a resume skips what it already did, a chunk that committed without its checkpoint self-heals,
// and a partially present chunk stops the run instead of being repaired.
//
// Skips when Docker is unavailable, like every other SQL suite here.
[Trait("Category", "SqlServer")]
[Collection("SqlSerial")]   // lnicoara/cmms#2148: only one SQL Edge container at a time
public sealed class LoadDataRunnerSqlTests : IClassFixture<LoadSqlEdgeFixture>, IAsyncLifetime, IDisposable
{
    private readonly LoadSqlEdgeFixture _fx;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loadrunner-sql-" + Guid.NewGuid().ToString("N"));

    public LoadDataRunnerSqlTests(LoadSqlEdgeFixture fx) => _fx = fx;

    // The fixture hands the whole CLASS one database, so without this each test inherits whatever the
    // previous one loaded and every row-count assertion is about the wrong thing.
    //
    // Order comes from the MODEL, not from a list written down here. It used to be twelve names, which was
    // fine while the generator emitted twelve tables and broke the moment it emitted 158: the delete hit
    // FK_WorkOrderSignatures_WorkOrders_WorkOrderId because WorkOrderSignatures was not on the list and so
    // was never cleared. A hand-written teardown is a second copy of the schema that nothing keeps honest.
    // lnicoara/cmms#2993.
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

    // Small enough to load in seconds, chunked small enough that resume and heal have several chunks to
    // work across rather than one.
    private string BuildArtifact(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var options = new GeneratorOptions
        {
            Seed = "sql-loader-test", OutDir = dir,
            WorkOrders = 120, Assets = 60, Users = 5,
            Campuses = 1, BuildingsPerCampus = 2, FloorsPerBuilding = 2, RoomsPerFloor = 3,
            Manufacturers = 4, ModelsPerManufacturer = 2, AssetTypes = 5, Accounts = 3,
            RowsPerChunk = 25,
        };
        var stats = new Generator(options).Run();
        ArtifactManifest.Write(dir, options, stats.TableTotals);
        return dir;
    }

    private string Conn()
    {
        using var db = _fx.NewContext();
        return db.Database.GetConnectionString()!;
    }

    private async Task<LoadReport> RunAsync(
        string artifactDir, bool execute, ICheckpointStore checkpoints, LoaderOptions? opts = null)
    {
        var artifact = Artifact.Open(artifactDir);
        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var options = opts ?? new LoaderOptions();
        options.Execute = execute;
        var target = new TargetDatabase(Conn(), 120);
        return await new Loader(artifact, plan, target, checkpoints, options).RunAsync(CancellationToken.None);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(*) FROM [{table}]";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // The claim the whole artifact format rests on: what comes out of SQL Server is what went into the
    // JSON. Checked per column against the artifact's own bytes rather than against values invented here,
    // and across every distinct CLR type the tables use.
    [SkippableFact]
    public async Task Loads_an_artifact_and_every_value_survives_the_round_trip()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("roundtrip");
        var artifact = Artifact.Open(dir);
        var report = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        Assert.True(report.Ok, string.Join("\n", report.Lines));
        Assert.Equal(artifact.ManifestTotalRows, report.RowsLoaded);

        foreach (var table in artifact.Tables)
            Assert.Equal(artifact.RowsFor(table), await CountAsync(table));

        // Now the values themselves, for one work order picked out of the artifact.
        var expected = FirstRow(dir, "WorkOrders");
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM [WorkOrders] WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", Guid.Parse(expected["Id"].GetString()!));
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), "the work order was not found after loading");

        var compared = 0;
        for (var i = 0; i < r.FieldCount; i++)
        {
            var column = r.GetName(i);
            if (!expected.TryGetValue(column, out var json)) continue;   // store-generated, not in the artifact

            if (json.ValueKind == JsonValueKind.Null) { Assert.True(await r.IsDBNullAsync(i), column); compared++; continue; }
            Assert.False(await r.IsDBNullAsync(i), $"{column} is null in SQL but not in the artifact");

            var actual = r.GetValue(i);
            switch (actual)
            {
                case Guid g: Assert.Equal(Guid.Parse(json.GetString()!), g); break;
                case DateTimeOffset dto: Assert.Equal(json.GetDateTimeOffset(), dto); break;
                case DateTime dt when r.GetDataTypeName(i) == "date":
                    Assert.Equal(DateOnly.Parse(json.GetString()!), DateOnly.FromDateTime(dt)); break;
                case DateTime dt: Assert.Equal(json.GetDateTime(), dt); break;
                case bool b: Assert.Equal(json.GetBoolean(), b); break;
                case string s: Assert.Equal(json.GetString(), s); break;
                case int n: Assert.Equal(json.GetInt32(), n); break;
                case long n: Assert.Equal(json.GetInt64(), n); break;
                case short n: Assert.Equal(json.GetInt16(), n); break;
                case byte n: Assert.Equal(json.GetByte(), n); break;
                case decimal d: Assert.Equal(json.GetDecimal(), d); break;
                default: continue;   // nothing this suite claims to cover
            }
            compared++;
        }
        Assert.True(compared > 20, $"only {compared} columns were actually compared");
    }

    // The default, and the reason it is the default: a run that has not been told to write must not write.
    [SkippableFact]
    public async Task Plan_only_writes_nothing()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("planonly");
        var report = await RunAsync(dir, execute: false, new MemoryCheckpointStore());

        Assert.True(report.Ok, string.Join("\n", report.Lines));
        Assert.Equal(0, report.RowsLoaded);
        Assert.Contains(report.Lines, l => l.Contains("PLAN ONLY"));
        foreach (var table in Artifact.Open(dir).Tables)
            Assert.Equal(0, await CountAsync(table));
    }

    // lnicoara/cmms#3054. A duplicate key is the ONE bulk-copy failure that means "this work is already
    // done", and ending a multi-hour load over it destroys hours of completed work to avoid re-doing a
    // fraction of a second of it. Seen for real at 10.7 million AuditEvents rows in.
    //
    // The scenario has to be built precisely, because the loader is good at NOT reaching this. Preflight
    // probes three keys per chunk (first, middle, last): all present adopts the chunk as already-landed,
    // some present is a partial-chunk refusal, none present means load it. So a duplicate arrives only when
    // every PROBED key is absent while other rows in the chunk are present, which is what deleting exactly
    // the probed rows reproduces. Simply loading twice does not do it; the probe adopts everything and no
    // insert is ever attempted.
    //
    // Against real SQL because that is the only place a PRIMARY KEY violation exists. A mocked
    // SqlException would assert that this code catches an exception it constructed itself.
    [SkippableFact]
    public async Task A_chunk_whose_rows_are_already_present_is_skipped_and_the_load_continues()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("dupes");
        var artifact = Artifact.Open(dir);

        var first = await RunAsync(dir, execute: true, new MemoryCheckpointStore());
        Assert.True(first.Ok, string.Join("\n", first.Lines));
        var loadedOnce = await CountAsync("AuditEvents");
        Assert.Equal(artifact.RowsFor("AuditEvents"), loadedOnce);

        // Now construct the ONE condition that actually reaches the duplicate path, because the loader is
        // good at not reaching it. Preflight probes three keys per chunk (first, middle, last): all present
        // adopts the chunk, some present is a partial-chunk refusal, none present means load it. So a
        // duplicate needs every PROBED key absent while a NON-sampled row is present.
        //
        // Start from an empty table and insert exactly one row, taking its Id from line index 1 of a chunk
        // — the second row, which the probe never samples.
        await using (var conn = new SqlConnection(Conn()))
        {
            await conn.OpenAsync();
            await using var wipe = conn.CreateCommand();
            wipe.CommandText = "DELETE FROM [AuditEvents]";
            await wipe.ExecuteNonQueryAsync();
        }

        var chunkPath = Directory.GetFiles(Path.Combine(dir, "AuditEvents"), "AuditEvents-*.jsonl.gz")
            .OrderBy(x => x, StringComparer.Ordinal).First();
        string secondRow;
        using (var gz = new GZipStream(File.OpenRead(chunkPath), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
        {
            await reader.ReadLineAsync();                       // index 0, which IS probed
            secondRow = (await reader.ReadLineAsync())!;        // index 1, which is not
        }
        var collidingId = JsonDocument.Parse(secondRow).RootElement.GetProperty("Id").GetGuid();

        await using (var db = _fx.NewContext())
        {
            db.Set<Cmms.Domain.Auditing.AuditEvent>().Add(new Cmms.Domain.Auditing.AuditEvent
            {
                Id = collidingId,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                EntityType = "WorkOrder",
                EntityId = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await CountAsync("AuditEvents"));

        // Empty checkpoints, so the loader believes it has everything to do.
        var second = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        // It must FINISH rather than end on the first collision, which is what it did before this change.
        Assert.True(second.Ok, string.Join("\n", second.Lines));
        Assert.True(second.ChunksDuplicate > 0,
            "expected duplicate chunks to be reported; got none, so the collision never happened and this " +
            "test is not exercising what it claims. Lines:\n" + string.Join("\n", second.Lines));

        // Said out loud, and as its own warning rather than folded into a line that reads like success.
        Assert.Contains(second.Lines, l => l.Contains("duplicate:") && l.Contains("skipping it and continuing"));
        Assert.Contains(second.Lines, l => l.Contains("not fully represented"));
    }

    // lnicoara/cmms#2993, replacing Refuses_a_target_whose_tables_are_not_empty.
    //
    // Grill round 4 made "this table holds rows I did not write" a refusal, under the rule that the artifact
    // owns the tables it targets. That rule is wrong for a LOAD TEST: rows somebody else put there are more
    // rows under load, which is the quantity being measured. Its real cost was that a target had to be
    // EMPTIED to be loadable, and emptying is how a failed load left pre-prod holding less than it started
    // with. The load now goes on top, and the pre-existing row is still there when it finishes.
    [SkippableFact]
    public async Task Loads_onto_a_target_that_already_holds_rows_it_did_not_write()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("nonempty");
        var artifact = Artifact.Open(dir);
        await using (var db = _fx.NewContext())
        {
            db.ServiceLines.Add(new Cmms.Domain.Organization.ServiceLine { Name = "Pre-existing", Code = "PX" });
            await db.SaveChangesAsync();
        }

        var report = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        Assert.True(report.Ok, string.Join("\n", report.Lines));
        Assert.Equal(artifact.ManifestTotalRows, report.RowsLoaded);
        // Said out loud rather than silently tolerated: a count that surprises the operator is worth seeing.
        Assert.Contains(report.Lines, l => l.Contains("note:") && l.Contains("ServiceLines"));

        // The artifact's rows landed, and the row that was already there is untouched. Nothing was deleted
        // to make room, which is the whole point of the change.
        Assert.Equal(artifact.RowsFor("WorkOrders"), await CountAsync("WorkOrders"));
        Assert.Equal(artifact.RowsFor("ServiceLines") + 1, await CountAsync("ServiceLines"));
        await using (var db = _fx.NewContext())
            Assert.True(await db.ServiceLines.AnyAsync(s => s.Code == "PX"));
    }

    // Grill round 4: a resume finds its target non-empty BY DESIGN, and the one invariant has to accept
    // that without a mode flag. Re-running a finished load must be a no-op, not a duplicate.
    [SkippableFact]
    public async Task Resuming_a_finished_load_skips_every_chunk_and_duplicates_nothing()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("resume");
        var checkpoints = new MemoryCheckpointStore();
        var first = await RunAsync(dir, execute: true, checkpoints);
        Assert.True(first.Ok, string.Join("\n", first.Lines));

        var counts = new Dictionary<string, long>();
        foreach (var t in Artifact.Open(dir).Tables) counts[t] = await CountAsync(t);

        var second = await RunAsync(dir, execute: true, checkpoints);

        Assert.True(second.Ok, string.Join("\n", second.Lines));
        Assert.Equal(0, second.ChunksLoaded);
        Assert.Equal(first.ChunksLoaded, second.ChunksSkipped);
        foreach (var (table, before) in counts) Assert.Equal(before, await CountAsync(table));
    }

    // The one genuinely ambiguous state from grill round 2: the chunk committed but the checkpoint write
    // did not land. The restart must recognise the chunk is already there and record it, NOT load it twice.
    [SkippableFact]
    public async Task Heals_a_chunk_that_committed_without_its_checkpoint()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("heal");

        // A store that accepts writes and forgets them, which is exactly a crash between commit and
        // checkpoint, repeated for every chunk.
        var forgetful = new MemoryCheckpointStore { Forget = true };
        var first = await RunAsync(dir, execute: true, forgetful);
        Assert.True(first.Ok, string.Join("\n", first.Lines));

        var artifact = Artifact.Open(dir);
        var loaded = new Dictionary<string, long>();
        foreach (var t in artifact.Tables) loaded[t] = await CountAsync(t);

        var real = new MemoryCheckpointStore();
        var second = await RunAsync(dir, execute: true, real);

        Assert.True(second.Ok, string.Join("\n", second.Lines));
        Assert.Equal(0, second.ChunksLoaded);
        Assert.Equal(first.ChunksLoaded, second.ChunksHealed);
        foreach (var (table, before) in loaded) Assert.Equal(before, await CountAsync(table));
    }

    // Under per-chunk atomicity a partial chunk cannot come from this loader, so it means something else
    // wrote to the table. Repairing it row by row would be guessing, so the run stops.
    [SkippableFact]
    public async Task Fails_hard_on_a_chunk_that_is_only_partially_present()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("partial");
        var forgetful = new MemoryCheckpointStore { Forget = true };
        Assert.True((await RunAsync(dir, execute: true, forgetful)).Ok);

        // Delete the LAST row of the first WorkOrders chunk. Its first key is still present and its last is
        // not, which is precisely the shape the probe must call partial.
        var artifact = Artifact.Open(dir);
        var chunk = artifact.Chunks["WorkOrders"][0];
        var lastId = LastRow(chunk.Path)["Id"].GetString()!;
        await using (var conn = new SqlConnection(Conn()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM [WorkOrders] WHERE [Id] = @id";
            cmd.Parameters.AddWithValue("@id", Guid.Parse(lastId));
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        var report = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        Assert.False(report.Ok);
        Assert.Contains(report.Lines, l => l.Contains("PARTIALLY present") && l.Contains("WorkOrders"));
    }

    // The v1 contract says a non-final chunk holds exactly rowsPerChunk rows, and the count derivation
    // trusts that WITHOUT measuring it. A truncated chunk would otherwise commit short, get checkpointed,
    // and be reported at its assumed count, leaving a wrong dataset that every later check agrees with.
    // The row count is verified inside the transaction, so a bad chunk rolls back and leaves nothing.
    [SkippableFact]
    public async Task Rolls_back_a_non_final_chunk_that_does_not_hold_the_rows_the_artifact_claims()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("truncated");

        // Drop one row from the FIRST WorkOrders chunk, which the v1 contract declares full. The manifest
        // is untouched, so nothing before the load can tell the count is now a lie.
        var chunk = Artifact.Open(dir).Chunks["WorkOrders"][0];
        var lines = ReadLines(chunk.Path);
        var claimed = lines.Count;
        lines.RemoveAt(lines.Count / 2);
        WriteLines(chunk.Path, lines);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(dir, execute: true, new MemoryCheckpointStore()));

        Assert.Contains($"{lines.Count:N0} rows", ex.Message);
        Assert.Contains($"{claimed:N0}", ex.Message);
        Assert.Equal(0, await CountAsync("WorkOrders"));   // rolled back, nothing left behind
    }

    // The probe samples first, middle and last rather than only the boundaries. A chunk missing rows in
    // between must not be adopted as fully present and quietly checkpointed.
    [SkippableFact]
    public async Task Detects_a_chunk_that_is_missing_rows_in_the_middle()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("middle");
        var forgetful = new MemoryCheckpointStore { Forget = true };
        Assert.True((await RunAsync(dir, execute: true, forgetful)).Ok);

        // Delete the row the probe samples from the middle, leaving both boundary keys in place. Under a
        // first-and-last-only probe this chunk would look complete.
        var chunk = Artifact.Open(dir).Chunks["WorkOrders"][0];
        var lines = ReadLines(chunk.Path);
        var middleId = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[lines.Count / 2])!["Id"].GetString()!;
        await using (var conn = new SqlConnection(Conn()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM [WorkOrders] WHERE [Id] = @id";
            cmd.Parameters.AddWithValue("@id", Guid.Parse(middleId));
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        var report = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        Assert.False(report.Ok);
        Assert.Contains(report.Lines, l => l.Contains("PARTIALLY present") && l.Contains("WorkOrders"));
    }

    // THE scenario the pre-prod load actually is, and it no longer needs a clear. lnicoara/cmms#2993.
    //
    // demo-health is seeded, and DemoDataSeeder writes ServiceLines rows at ServiceLineSeed's well-known
    // Guids with codes "CE" and "FE". The artifact used to emit exactly those values, so a load collided
    // twice over: on the primary key, and on IX_ServiceLines_Code, which is UNIQUE. Emptying the tenant was
    // the answer to a problem the generator was creating.
    //
    // Both are seed-derived now, so this asserts the load simply works against a target seeded the way the
    // real tenant is, with nothing deleted. That is the test the old Clearing_a_seeded_target_makes_it_
    // loadable becomes.
    [SkippableFact]
    public async Task Loads_onto_a_seeded_target_without_clearing_anything()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("seededtarget");
        var artifact = Artifact.Open(dir);

        // Seed the target the way the real tenant is seeded: the same two service lines, on the same Guids
        // and the same codes the seeder writes. These are the exact values the artifact used to claim.
        await using (var db = _fx.NewContext())
        {
            db.ServiceLines.Add(new Cmms.Domain.Organization.ServiceLine
            {
                Id = Cmms.Domain.Organization.ServiceLineSeed.ClinicalEngineeringId,
                Name = "Clinical Engineering", Code = "CE",
            });
            db.ServiceLines.Add(new Cmms.Domain.Organization.ServiceLine
            {
                Id = Cmms.Domain.Organization.ServiceLineSeed.FacilitiesId,
                Name = "Facilities", Code = "FE",
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, await CountAsync("ServiceLines"));

        var loaded = await RunAsync(dir, execute: true, new MemoryCheckpointStore());

        Assert.True(loaded.Ok, string.Join("\n", loaded.Lines));
        Assert.Equal(artifact.ManifestTotalRows, loaded.RowsLoaded);
        Assert.DoesNotContain(loaded.Lines, l => l.Contains("REFUSING"));

        // Every artifact table holds its rows ON TOP of what the seeding put there, and the seeded service
        // lines are still present. Nothing was emptied to make this work.
        Assert.Equal(artifact.RowsFor("ServiceLines") + 2, await CountAsync("ServiceLines"));
        await using (var db = _fx.NewContext())
        {
            Assert.True(await db.ServiceLines.AnyAsync(
                s => s.Id == Cmms.Domain.Organization.ServiceLineSeed.FacilitiesId));
            Assert.True(await db.ServiceLines.AnyAsync(
                s => s.Id == Cmms.Domain.Organization.ServiceLineSeed.ClinicalEngineeringId));
        }
    }

    // The clear still exists and still works; it is simply no longer the price of loading. Kept as its own
    // test so removing the load path's dependence on it did not quietly remove its coverage.
    [SkippableFact]
    public async Task Clearing_a_seeded_target_still_empties_it()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("clearseeded");
        var artifact = Artifact.Open(dir);

        await using (var db = _fx.NewContext())
        {
            db.ServiceLines.Add(new Cmms.Domain.Organization.ServiceLine
            {
                Id = Cmms.Domain.Organization.ServiceLineSeed.FacilitiesId,
                Name = "Facilities", Code = "FE",
            });
            await db.SaveChangesAsync();
        }

        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var target = new TargetDatabase(Conn(), 120);
        var clean = await TargetCleaner.ClearAsync(plan, model, target, execute: true, CancellationToken.None);

        Assert.True(clean.Executed);
        Assert.Equal(1, clean.Cleared.Single(c => c.Table == "ServiceLines").Rows);
        foreach (var t in artifact.Tables) Assert.Equal(0, await CountAsync(t));

        var loaded = await RunAsync(dir, execute: true, new MemoryCheckpointStore());
        Assert.True(loaded.Ok, string.Join("\n", loaded.Lines));
        Assert.Equal(artifact.ManifestTotalRows, loaded.RowsLoaded);
        foreach (var t in artifact.Tables) Assert.Equal(artifact.RowsFor(t), await CountAsync(t));
    }

    // Clearing is destructive, so it takes two independent yeses. The clear flag alone must not delete.
    [SkippableFact]
    public async Task Clearing_without_execute_deletes_nothing()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = BuildArtifact("clearplan");
        await using (var db = _fx.NewContext())
        {
            db.ServiceLines.Add(new Cmms.Domain.Organization.ServiceLine { Name = "Keep me", Code = "KM" });
            await db.SaveChangesAsync();
        }

        using var model = new ModelBridge();
        var plan = LoadPlan.Build(Artifact.Open(dir), model);
        var clean = await TargetCleaner.ClearAsync(
            plan, model, new TargetDatabase(Conn(), 120), execute: false, CancellationToken.None);

        Assert.False(clean.Executed);
        Assert.Empty(clean.Cleared);
        Assert.Contains(clean.Lines, l => l.Contains("PLAN:") && l.Contains("Nothing deleted"));
        Assert.Equal(1, await CountAsync("ServiceLines"));
    }

    private static List<string> ReadLines(string gzPath)
    {
        var lines = new List<string>();
        using var file = File.OpenRead(gzPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static void WriteLines(string gzPath, IEnumerable<string> lines)
    {
        using var file = File.Create(gzPath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip);
        foreach (var line in lines) writer.WriteLine(line);
    }

    private static Dictionary<string, JsonElement> FirstRow(string dir, string table)
    {
        var path = Directory.GetFiles(Path.Combine(dir, table), $"{table}-*.jsonl.gz")
            .OrderBy(f => f, StringComparer.Ordinal).First();
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.ReadLine()!)!;
    }

    private static Dictionary<string, JsonElement> LastRow(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        string? last = null, line;
        while ((line = reader.ReadLine()) is not null) last = line;
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(last!)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// An in-process checkpoint store. <see cref="Forget"/> makes every write silently vanish, which is how
    /// a crash between the chunk's commit and its checkpoint is reproduced deterministically rather than
    /// by trying to kill the loader at the right microsecond.
    /// </summary>

}
