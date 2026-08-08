using System.IO.Compression;
using System.Text.Json;
using Cmms.LoadDataGenerator;

namespace Cmms.LoadDataRunner.Tests;

// lnicoara/cmms#2908. These build a REAL artifact with the real generator (and the real manifest writer)
// rather than a hand-rolled directory, because the thing under test is precisely whether the loader's
// reading of an artifact agrees with the generator's writing of one.
public class ArtifactTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loader-tests-" + Guid.NewGuid().ToString("N"));

    // rowsPerChunk far below the row counts, so every table lands several chunks and the last one is short.
    // At the production 100,000 every small table would be a single chunk and the derivation this suite
    // exists to check would never be exercised.
    internal static GeneratorOptions Small(string outDir) => new()
    {
        Seed = "loader-test-seed", OutDir = outDir,
        WorkOrders = 300, Assets = 120, Users = 6,
        Campuses = 2, BuildingsPerCampus = 2, FloorsPerBuilding = 2, RoomsPerFloor = 3,
        Manufacturers = 5, ModelsPerManufacturer = 3, AssetTypes = 6, Accounts = 4,
        RowsPerChunk = 37,
    };

    internal static string Build(string dir, Action<GeneratorOptions>? tweak = null)
    {
        var options = Small(dir);
        tweak?.Invoke(options);
        Directory.CreateDirectory(dir);
        var stats = new Generator(options).Run();
        ArtifactManifest.Write(dir, options, stats.TableTotals);
        return dir;
    }

    [Fact]
    public void Derives_exact_per_chunk_counts_without_reading_every_chunk()
    {
        var a = Artifact.Open(Build(Path.Combine(_dir, "a")));

        foreach (var (table, chunks) in a.Chunks)
        {
            // The v1 contract: full except the last.
            foreach (var c in chunks.Take(chunks.Count - 1))
                Assert.Equal(a.RowsPerChunk, c.Rows);

            Assert.InRange(chunks[^1].Rows, 1, a.RowsPerChunk);

            // And the derivation matches the ground truth of actually decompressing every chunk.
            Assert.Equal(ActualRows(a.Directory, table), a.RowsFor(table));
        }
    }

    [Fact]
    public void Derived_total_equals_the_manifest_total()
    {
        var a = Artifact.Open(Build(Path.Combine(_dir, "b")));
        Assert.Equal(a.ManifestTotalRows, a.Tables.Sum(a.RowsFor));
    }

    // Deleting the FINAL chunk of a table leaves the numbering intact (1..N-1), so the gap check cannot see
    // it. The manifest-total comparison is the only thing that catches it, which is why it is not optional.
    [Fact]
    public void Rejects_an_artifact_missing_its_final_chunk()
    {
        var dir = Build(Path.Combine(_dir, "c"));
        var chunks = Directory.GetFiles(Path.Combine(dir, "WorkOrders")).OrderBy(f => f, StringComparer.Ordinal).ToList();
        File.Delete(chunks[^1]);

        var ex = Assert.Throws<InvalidOperationException>(() => Artifact.Open(dir));
        Assert.Contains("manifest.json declares", ex.Message);
    }

    // Deleting a MIDDLE chunk is caught earlier and more precisely, by the numbering running 1, 2, 4.
    [Fact]
    public void Rejects_an_artifact_with_a_gap_in_its_chunk_numbering()
    {
        var dir = Build(Path.Combine(_dir, "d"));
        var chunks = Directory.GetFiles(Path.Combine(dir, "WorkOrders")).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(chunks.Count >= 3, "the fixture must produce enough chunks to remove a middle one");
        File.Delete(chunks[1]);

        var ex = Assert.Throws<InvalidOperationException>(() => Artifact.Open(dir));
        Assert.Contains("chunk file is missing", ex.Message);
    }

    [Fact]
    public void Rejects_an_unsupported_format_version()
    {
        var dir = Build(Path.Combine(_dir, "e"));
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"v1\"", "\"v2\""));

        var ex = Assert.Throws<InvalidOperationException>(() => Artifact.Open(dir));
        Assert.Contains("is not supported", ex.Message);
    }

    // The checkpoint key is (artifact hash, database). If a regenerated artifact hashed the same as the one
    // before it, a resume would skip chunks it never loaded, silently.
    [Fact]
    public void Hash_is_stable_for_one_seed_and_changes_with_the_seed()
    {
        var same1 = Artifact.Open(Build(Path.Combine(_dir, "f")));
        var same2 = Artifact.Open(Build(Path.Combine(_dir, "g")));
        var other = Artifact.Open(Build(Path.Combine(_dir, "h"), o => o.Seed = "a-different-seed"));

        Assert.Equal(same1.Hash, same2.Hash);
        Assert.NotEqual(same1.Hash, other.Hash);
    }

    // The hole a manifest-only hash leaves. The manifest pins the seed and every generation parameter, so
    // two runs of ONE generator agree, but a change to the generator itself produces different bytes under
    // a byte-identical manifest. Since the hash keys the checkpoint set, that would let a regenerated
    // artifact inherit the old one's checkpoints and skip chunks whose contents changed underneath it.
    // Simulated here by rewriting chunk bytes while leaving manifest.json untouched. lnicoara/cmms#2908
    [Fact]
    public void Hash_changes_when_chunk_contents_change_under_an_identical_manifest()
    {
        var dir = Build(Path.Combine(_dir, "content"));
        var before = Artifact.Open(dir);
        var manifestBefore = File.ReadAllBytes(Path.Combine(dir, "manifest.json"));

        var chunk = Directory.GetFiles(Path.Combine(dir, "WorkOrders"))
            .OrderBy(f => f, StringComparer.Ordinal).First();
        var lines = ReadLines(chunk);
        lines[0] = lines[0].Replace("\"WO-", "\"XX-");
        WriteLines(chunk, lines);

        var after = Artifact.Open(dir);

        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(dir, "manifest.json")));
        Assert.NotEqual(before.Hash, after.Hash);
    }

    [Fact]
    public void Hash_is_unchanged_by_rereading_the_same_bytes()
    {
        var dir = Build(Path.Combine(_dir, "stable"));
        Assert.Equal(Artifact.Open(dir).Hash, Artifact.Open(dir).Hash);
    }

    [Fact]
    public void Rejects_a_directory_that_is_not_an_artifact()
    {
        var dir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(dir);
        var ex = Assert.Throws<InvalidOperationException>(() => Artifact.Open(dir));
        Assert.Contains("No manifest.json", ex.Message);
    }

    internal static List<string> ReadLines(string gzPath)
    {
        var lines = new List<string>();
        using var file = File.OpenRead(gzPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    internal static void WriteLines(string gzPath, IEnumerable<string> lines)
    {
        using var file = File.Create(gzPath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip);
        foreach (var line in lines) writer.WriteLine(line);
    }

    private static long ActualRows(string dir, string table)
    {
        long n = 0;
        foreach (var f in Directory.GetFiles(Path.Combine(dir, table), $"{table}-*.jsonl.gz"))
        {
            using var file = File.OpenRead(f);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            while (reader.ReadLine() is not null) n++;
        }
        return n;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
