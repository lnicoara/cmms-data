using System.Text.Json;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Writes manifest.json, the artifact's self-description and the only file a reader must understand
/// before it can understand the rest.
///
/// It lives here rather than inline in Program so the loader's tests can build a REAL artifact rather than
/// a hand-rolled imitation of one. A test fixture that writes its own manifest proves the loader works
/// against that fixture, which is not the same claim.
///
/// THE v1 FORMAT CONTRACT, which the loader depends on and lnicoara/cmms#2908 grill round 5 promoted out
/// of implementation detail:
///
///   formatVersion "v1" together with rowsPerChunk MEANS that for every table, each chunk file except the
///   last holds exactly rowsPerChunk rows, and only the final chunk may be short.
///
/// That is what lets a loader derive exact per-chunk row counts from a directory listing plus one file
/// read per table instead of decompressing all 438 of them. It is guaranteed by TableWriter rolling only
/// when a chunk is full, and pinned by a test. A future writer that rolled on compressed size instead
/// would not be a compatible change to v1; it would be v2.
/// </summary>
public static class ArtifactManifest
{
    public const string FormatVersion = "v1";

    public static void Write(string outDir, GeneratorOptions options, IReadOnlyDictionary<string, long> tableTotals,
        TargetBuild.Resolved? target = null)
    {
        var manifest = new
        {
            formatVersion = FormatVersion,
            seed = options.Seed,
            generationDate = options.GenerationDate.ToString("yyyy-MM-dd"),
            generatedTables = tableTotals.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            rowsPerChunk = options.RowsPerChunk,
            totalRows = tableTotals.Values.Sum(),
            // The covered set is recorded rather than implied: the tenant model has 162 tables and this emits
            // the volume tables plus the spine they need. A reader must be able to see the gap, not assume
            // there is none.
            // Coverage, recorded so a reader can see the gap rather than assume there is none. It used to
            // say only "162 tables exist"; it now says which are generated and which are exempt and why,
            // because the previous shape let 148 empty tables read as a complete artifact.
            tenantModelTables = tableTotals.Count + TableCoverage.Exempt.Count,
            exemptTables = TableCoverage.Exempt
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            pendingTables = TableCoverage.Pending.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            // Which build this artifact fits. An artifact on disk carries no evidence of the model it was
            // built from otherwise, so it could only be checked against a target by loading it.
            sourceCommit = target?.Commit,
            targetEnvironment = TargetBuild.Environment,
            // OutDir is where the artifact happens to sit, not what it contains, so it is excluded: the same
            // seed written to two directories must produce two byte-identical manifests.
            parameters = new Dictionary<string, object?>(
                typeof(GeneratorOptions).GetProperties()
                    .Where(pr => pr.Name != nameof(GeneratorOptions.OutDir))
                    .OrderBy(pr => pr.Name, StringComparer.Ordinal)
                    .Select(pr => new KeyValuePair<string, object?>(pr.Name, pr.GetValue(options)))),
            // Elapsed time and memory deliberately live in run.json, NOT here. The manifest is part of the
            // artifact and the artifact must be byte-identical for a given seed, so nothing that varies
            // between two runs of the same inputs belongs in it.
        };

        File.WriteAllText(
            Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
