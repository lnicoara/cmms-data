using Cmms.LoadDataGenerator;
using Microsoft.EntityFrameworkCore;

namespace Cmms.LoadDataRunner.SqlTests;

/// <summary>
/// The two things every loader SQL suite needs: a small artifact, and a way to run the loader against the
/// fixture's database. lnicoara/cmms#3054.
///
/// Shared rather than copied, because a second suite appeared the moment one test needed its own database
/// (a destructive test that drops a column poisons the class-shared one for everything after it), and two
/// private copies of the artifact builder would be free to drift on the next generator change while
/// looking identical.
/// </summary>
internal static class LoadSqlFixtures
{
    /// <summary>
    /// A deliberately tiny artifact: 25 rows per chunk, so a handful of tables produce many chunks and the
    /// chunk-level behaviour under test (probe, heal, partial, duplicate) is reachable in seconds.
    /// </summary>
    public static string BuildArtifact(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "loadrunner-sql-" + Guid.NewGuid().ToString("N"), name);
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

    public static async Task<LoadReport> RunAsync(
        LoadSqlEdgeFixture fx, string artifactDir, bool execute, ICheckpointStore? checkpoints = null)
    {
        var artifact = Artifact.Open(artifactDir);
        using var model = new ModelBridge();
        var plan = LoadPlan.Build(artifact, model);
        var options = new LoaderOptions { Execute = execute };
        var target = new TargetDatabase(fx.ConnectionString, 120);
        return await new Loader(
            artifact, plan, target, checkpoints ?? new MemoryCheckpointStore(), options)
            .RunAsync(CancellationToken.None);
    }
}

/// <summary>
/// Checkpoints that live only for one test. Shared with the isolated suites so a second copy cannot
/// drift from this one. lnicoara/cmms#3054.
/// </summary>
internal sealed class MemoryCheckpointStore : ICheckpointStore
{
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    public bool Forget { get; init; }

    public Task<bool> IsCompleteAsync(string table, int chunk, CancellationToken ct) =>
        Task.FromResult(_done.Contains($"{table}/{chunk}"));

    public Task MarkCompleteAsync(string table, int chunk, string detail, CancellationToken ct)
    {
        if (!Forget) _done.Add($"{table}/{chunk}");
        return Task.CompletedTask;
    }
}
