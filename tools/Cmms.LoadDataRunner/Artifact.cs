using System.Security.Cryptography;
using System.Text.Json;

namespace Cmms.LoadDataRunner;

/// <summary>One chunk file, and how many rows it holds.</summary>
public sealed record ChunkRef(string Table, int Number, string Path, long Rows);

/// <summary>
/// The artifact on disk, read and validated.
///
/// The interesting work here is deriving per-chunk row counts, which the manifest does not record but the
/// preflight invariant needs (grill rounds 4 and 5). It is derivable exactly rather than approximately
/// because of what <c>formatVersion: v1</c> MEANS: every non-final chunk of a table holds exactly
/// <c>rowsPerChunk</c> rows, and only the final chunk may be short. That is the artifact contract, not an
/// implementation accident, and TableWriter's own tests pin it. A writer that later rolls on compressed
/// size would be a v2 format, not a compatible change.
///
/// So the cost is twelve file reads (the final chunk of each table) rather than all 438, and the derived sum
/// is checked against the manifest's own total, which catches a missing chunk, a misnamed file, or a
/// truncated write for free.
/// </summary>
public sealed class Artifact
{
    public const string SupportedFormatVersion = "v1";

    public string Directory { get; }
    public string FormatVersion { get; }
    public string Seed { get; }
    public int RowsPerChunk { get; }
    public long ManifestTotalRows { get; }

    /// <summary>Stable identity of this artifact's content, used to key its checkpoints.</summary>
    public string Hash { get; }

    /// <summary>Table to its chunks, in file order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChunkRef>> Chunks { get; }

    public IReadOnlyList<string> Tables => Chunks.Keys.ToList();

    public long RowsFor(string table) => Chunks[table].Sum(c => c.Rows);

    private Artifact(
        string dir, string formatVersion, string seed, int rowsPerChunk, long manifestTotalRows,
        string hash, IReadOnlyDictionary<string, IReadOnlyList<ChunkRef>> chunks)
    {
        Directory = dir;
        FormatVersion = formatVersion;
        Seed = seed;
        RowsPerChunk = rowsPerChunk;
        ManifestTotalRows = manifestTotalRows;
        Hash = hash;
        Chunks = chunks;
    }

    /// <summary>
    /// Opens and fully validates an artifact directory. Throws rather than returning a partly-trusted
    /// object: everything downstream assumes the chunk set is complete and the counts are exact.
    /// </summary>
    public static Artifact Open(string dir)
    {
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"No manifest.json in '{dir}'. That is not an artifact directory.");

        var bytes = File.ReadAllBytes(manifestPath);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        var formatVersion = Str(root, "formatVersion");
        if (formatVersion != SupportedFormatVersion)
            throw new InvalidOperationException(
                $"Artifact format '{formatVersion}' is not supported; this loader reads '{SupportedFormatVersion}'. " +
                "The row-count derivation is only valid for v1, so refusing rather than guessing.");

        var seed = Str(root, "seed");
        var rowsPerChunk = Int(root, "rowsPerChunk");
        if (rowsPerChunk <= 0)
            throw new InvalidOperationException($"manifest rowsPerChunk is {rowsPerChunk}; must be positive.");
        var manifestTotal = Long(root, "totalRows");

        var tables = root.GetProperty("generatedTables").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (tables.Count == 0)
            throw new InvalidOperationException("manifest generatedTables is empty; nothing to load.");

        var chunks = new Dictionary<string, IReadOnlyList<ChunkRef>>(StringComparer.Ordinal);
        foreach (var table in tables.OrderBy(t => t, StringComparer.Ordinal))
            chunks[table] = ChunksFor(dir, table, rowsPerChunk);

        // The derived total against the manifest's own. A mismatch means the chunk set on disk is not the
        // chunk set that was written, and no amount of careful loading recovers from that.
        var derivedTotal = chunks.Values.Sum(cs => cs.Sum(c => c.Rows));
        if (derivedTotal != manifestTotal)
            throw new InvalidOperationException(
                $"Artifact is inconsistent: chunk files hold {derivedTotal:N0} rows but manifest.json declares " +
                $"{manifestTotal:N0}. Missing, extra, or truncated chunk files.");

        return new Artifact(dir, formatVersion, seed, rowsPerChunk, manifestTotal, ContentHash(bytes, chunks), chunks);
    }

    /// <summary>
    /// Identity of what this artifact actually CONTAINS, not merely what it says about itself.
    ///
    /// The manifest alone is not enough, and the gap is not theoretical. It pins the seed and every
    /// generation parameter, so two runs of one generator agree, but a change to the GENERATOR ITSELF
    /// produces different bytes under a byte-identical manifest. Since this hash keys the checkpoint set,
    /// a manifest-only hash would let a regenerated artifact inherit the previous one's checkpoints and
    /// skip chunks whose contents have changed underneath it. That is a silently wrong dataset, which is
    /// the single most expensive failure this tool can produce.
    ///
    /// The content digest is nearly free because gzip already computed it: every member ends with a CRC32
    /// of its UNCOMPRESSED bytes plus the uncompressed length. Reading eight bytes from the tail of each
    /// of the 438 chunk files fingerprints the whole 3.7 GB without decompressing any of it.
    /// </summary>
    private static string ContentHash(byte[] manifestBytes, IReadOnlyDictionary<string, IReadOnlyList<ChunkRef>> chunks)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(manifestBytes, 0, manifestBytes.Length, null, 0);

        foreach (var table in chunks.Keys.OrderBy(k => k, StringComparer.Ordinal))
        foreach (var chunk in chunks[table])
        {
            var stamp = System.Text.Encoding.UTF8.GetBytes(
                $"{table}/{chunk.Number}:{GzipFingerprint(chunk.Path)}");
            sha.TransformBlock(stamp, 0, stamp.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant()[..16];
    }

    /// <summary>The gzip trailer: CRC32 of the uncompressed bytes and their length, plus the file size.</summary>
    private static string GzipFingerprint(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 8) throw new InvalidOperationException($"Chunk '{path}' is truncated: {file.Length} bytes.");

        Span<byte> trailer = stackalloc byte[8];
        file.Seek(-8, SeekOrigin.End);
        file.ReadExactly(trailer);

        var crc = BitConverter.ToUInt32(trailer[..4]);
        var uncompressedLength = BitConverter.ToUInt32(trailer[4..]);
        return $"{crc:x8}-{uncompressedLength:x8}-{file.Length:x}";
    }

    private static IReadOnlyList<ChunkRef> ChunksFor(string dir, string table, int rowsPerChunk)
    {
        var tableDir = Path.Combine(dir, table);
        if (!System.IO.Directory.Exists(tableDir))
            throw new InvalidOperationException($"Manifest lists table '{table}' but '{tableDir}' does not exist.");

        var files = System.IO.Directory.GetFiles(tableDir, $"{table}-*.jsonl.gz")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException($"Table '{table}' has no chunk files in '{tableDir}'.");

        // Chunk numbers must run 1..N with no gap. A gap means a lost file, and since every non-final
        // chunk is assumed full, a silent gap would corrupt the expected count for the whole table.
        var refs = new List<ChunkRef>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(files[i]));
            var numberPart = name[(table.Length + 1)..];
            if (!int.TryParse(numberPart, out var number))
                throw new InvalidOperationException($"Chunk file '{files[i]}' does not carry a parsable chunk number.");
            if (number != i + 1)
                throw new InvalidOperationException(
                    $"Table '{table}' chunk numbering jumps to {number} at position {i + 1}. A chunk file is missing.");

            // Every chunk but the last is full BY CONTRACT (v1). Only the last is counted, and counting it
            // means decompressing one file rather than all of them.
            var rows = i == files.Count - 1 ? CountRows(files[i]) : rowsPerChunk;
            refs.Add(new ChunkRef(table, number, files[i], rows));
        }
        return refs;
    }

    private static long CountRows(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        long n = 0;
        while (reader.ReadLine() is not null) n++;
        return n;
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()!
            : throw new InvalidOperationException($"manifest.json is missing string property '{name}'.");

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : throw new InvalidOperationException($"manifest.json is missing numeric property '{name}'.");

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt64()
            : throw new InvalidOperationException($"manifest.json is missing numeric property '{name}'.");
}
