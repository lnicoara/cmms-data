using System.Diagnostics;
using System.Text.Json;
using Cmms.LoadDataGenerator;
using Microsoft.Extensions.Configuration;

// lnicoara/cmms#2876, per epic #2731. Offline synthetic-data generator.
//
// Opens no database connection and reads no Azure configuration on purpose, so it runs and is validated
// on a laptop with only the .NET SDK. Loading the artifact is a separate concern and is not here.

// An EXPLICITLY requested profile that cannot be found is an error, not an absence. lnicoara/cmms#2969
//
// Both JSON layers used to be optional, and the paths resolve relative to the process base directory rather
// than the repo root, so a profile named from the wrong directory was skipped in silence and the run
// proceeded on GeneratorOptions defaults. It printed a banner, self-validated, and reported success.
//
// The result is worse than a crash, because the defaults carry Seed = "cmms-loadtest-v1", which is the FULL
// profile's seed. Keys are a pure function of (seed, kind, ordinal), so a small artifact stamped with that
// seed has a key space that is a strict SUBSET of the full artifact's. The loader's chunk-presence probe is
// a bare primary-key lookup, so it can adopt the full artifact's rows as this one's and write checkpoints
// that nothing can delete, permanently poisoning the artifact against that database.
//
// params.json stays optional: an IMPLICIT default that is absent is a legitimate bare run. The distinction
// is between what the operator asked for and what merely might have been there.
// RESOLVED here to an absolute path, rather than handed to the configuration builder as written, because
// the two disagree about what a relative path means. File.Exists resolves from the working directory, while
// ConfigurationBuilder resolves from AppContext.BaseDirectory, which is the bin output. The profiles are
// copied there (CopyToOutputDirectory in the csproj), so a bare "small.json" happens to work while
// "tools/Cmms.LoadDataGenerator/small.json" silently does not, even though the file plainly exists and a
// naive existence check says so. Both roots are tried, and the absolute result removes the ambiguity.
var profileRequest = ProfileResolver.Resolve(
    Environment.GetEnvironmentVariable("GENERATOR_PARAMS"),
    [AppContext.BaseDirectory, Directory.GetCurrentDirectory()]);
if (!profileRequest.Ok)
{
    Console.Error.WriteLine(profileRequest.Error);
    return 1;
}
var resolvedProfile = profileRequest.Path;

var config = new ConfigurationBuilder()
    // params.json stays optional. An IMPLICIT default that is absent is a legitimate bare run; an EXPLICIT
    // request that is absent is not, which is why the profile above is required once it is named.
    .AddJsonFile("params.json", optional: true)
    .AddJsonFile(resolvedProfile ?? "none.json", optional: resolvedProfile is null)
    .AddEnvironmentVariables()
    .Build();

var options = new GeneratorOptions();
config.Bind(options);
foreach (var a in args)
{
    if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
    var kv = a[2..].Split('=', 2);
    // Rejected, not skipped. "--Assets 500" (a space instead of '=') used to fall through this branch
    // silently and the run proceeded at the DEFAULT count, producing a wrong-sized artifact that looks
    // deliberate. An override that cannot be honoured has to stop the run.
    if (kv.Length != 2)
    {
        Console.Error.WriteLine($"Malformed option '{a}'. Use --Name=Value, with no space around '='.");
        return 1;
    }
    var prop = typeof(GeneratorOptions).GetProperty(kv[0],
        System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    if (prop is null) { Console.Error.WriteLine($"Unknown option: {kv[0]}"); return 1; }
    object parsed = prop.PropertyType == typeof(int) ? int.Parse(kv[1])
        : prop.PropertyType == typeof(double) ? double.Parse(kv[1])
        : prop.PropertyType == typeof(DateOnly) ? DateOnly.Parse(kv[1])
        : kv[1];
    prop.SetValue(options, parsed);
}

if (string.IsNullOrWhiteSpace(options.Seed)) { Console.Error.WriteLine("Seed is required."); return 1; }

// EVERY cardinality option, and BEFORE the delete below. Only Assets was checked, so a profile that
// zeroed any of the others (which a naive percentage scaling does to six of them at once) destroyed the
// previous artifact and then threw deep inside Deterministic.Pick on an empty parent pool. The operator
// was left with no artifact and a stack trace that named neither the option nor the profile.
foreach (var (name, value) in new (string, int)[]
         {
             (nameof(options.WorkOrders), options.WorkOrders),
             (nameof(options.Assets), options.Assets),
             (nameof(options.Users), options.Users),
             (nameof(options.Campuses), options.Campuses),
             (nameof(options.BuildingsPerCampus), options.BuildingsPerCampus),
             (nameof(options.FloorsPerBuilding), options.FloorsPerBuilding),
             (nameof(options.RoomsPerFloor), options.RoomsPerFloor),
             (nameof(options.Manufacturers), options.Manufacturers),
             (nameof(options.ModelsPerManufacturer), options.ModelsPerManufacturer),
             (nameof(options.AssetTypes), options.AssetTypes),
             (nameof(options.Accounts), options.Accounts),
             (nameof(options.RowsPerChunk), options.RowsPerChunk),
         })
{
    if (value <= 0)
    {
        Console.Error.WriteLine($"{name} must be positive (got {value}). Nothing was deleted.");
        return 1;
    }
}

if (Directory.Exists(options.OutDir)) Directory.Delete(options.OutDir, recursive: true);
Directory.CreateDirectory(options.OutDir);

// Says what it LOADED, not only what it computed. A run that reports its numbers without naming their
// source cannot be told apart from a run that fell back to defaults, and telling those apart after the fact
// meant reading the manifest's seed. lnicoara/cmms#2969
Console.WriteLine($"profile={resolvedProfile ?? "(none, defaults plus params.json if present)"}");
Console.WriteLine($"seed={options.Seed}  workOrders={options.WorkOrders:N0}  assets={options.Assets:N0}  users={options.Users:N0}");
Console.WriteLine($"out={Path.GetFullPath(options.OutDir)}");

// Which build this artifact is for, resolved BEFORE any row is written. An artifact generated against a
// model the target does not have fails inside a container job after a multi-gigabyte upload, so the cheap
// place to find out is here. lnicoara/cmms#2993
var target = TargetBuild.ResolveTarget();
Console.WriteLine($"target={target.Commit}  ({target.Source})");

var sw = Stopwatch.StartNew();
var stats = new Generator(options).Run();
sw.Stop();

// Force a full collection first so the number reflects retained memory rather than uncollected
// garbage, which is what matters for whether this fits in a container.
var heapMb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
var peakMb = Environment.WorkingSet / 1024.0 / 1024.0;
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

// Shared with the loader's tests so they exercise a REAL artifact rather than an imitation of one.
ArtifactManifest.Write(options.OutDir, options, stats.TableTotals, target);
File.WriteAllText(Path.Combine(options.OutDir, "stats.json"), JsonSerializer.Serialize(stats.ToReport(options), jsonOpts));
File.WriteAllText(Path.Combine(options.OutDir, "run.json"), JsonSerializer.Serialize(new
{
    elapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
    peakWorkingSetMb = Math.Round(peakMb, 1),
    retainedHeapMb = Math.Round(heapMb, 1),
}, jsonOpts));

Console.WriteLine($"\n{stats.TableTotals.Values.Sum():N0} rows in {sw.Elapsed.TotalSeconds:N1}s, retained heap {heapMb:N0} MB, working set {peakMb:N0} MB");
foreach (var (table, rows) in stats.TableTotals.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {table,-18} {rows,12:N0}");

var result = Validator.Run(options.OutDir);
Console.WriteLine();
foreach (var line in result.Lines) Console.WriteLine(line);
if (!result.Ok) { Console.Error.WriteLine("\nSELF-VALIDATION FAILED"); return 1; }
Console.WriteLine("\nself-validation passed");
return 0;
