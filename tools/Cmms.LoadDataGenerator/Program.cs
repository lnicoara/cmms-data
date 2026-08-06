using System.Diagnostics;
using System.Text.Json;
using Cmms.LoadDataGenerator;
using Microsoft.Extensions.Configuration;

// lnicoara/cmms#2876, per epic #2731. Offline synthetic-data generator.
//
// Opens no database connection and reads no Azure configuration on purpose, so it runs and is validated
// on a laptop with only the .NET SDK. Loading the artifact is a separate concern and is not here.

var config = new ConfigurationBuilder()
    .AddJsonFile("params.json", optional: true)
    .AddJsonFile(Environment.GetEnvironmentVariable("GENERATOR_PARAMS") ?? "none.json", optional: true)
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

Console.WriteLine($"seed={options.Seed}  workOrders={options.WorkOrders:N0}  assets={options.Assets:N0}  users={options.Users:N0}");
Console.WriteLine($"out={Path.GetFullPath(options.OutDir)}");

var sw = Stopwatch.StartNew();
var stats = new Generator(options).Run();
sw.Stop();

// Force a full collection first so the number reflects retained memory rather than uncollected
// garbage, which is what matters for whether this fits in a container.
var heapMb = GC.GetTotalMemory(forceFullCollection: true) / 1024.0 / 1024.0;
var peakMb = Environment.WorkingSet / 1024.0 / 1024.0;
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

// Shared with the loader's tests so they exercise a REAL artifact rather than an imitation of one.
ArtifactManifest.Write(options.OutDir, options, stats.TableTotals);
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
