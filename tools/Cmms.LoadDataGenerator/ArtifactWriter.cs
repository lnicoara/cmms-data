using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Writes one table's rows as gzipped JSON Lines, split into fixed-size chunks.
///
/// Streaming by construction: a row is serialized, written, and forgotten. Nothing accumulates, so peak
/// memory is flat whether the table has a thousand rows or four million. That is not a micro-optimization.
/// The environment this eventually runs in caps a replica at 8 GiB, and the repo already has an
/// OutOfMemoryException on record from a job that buffered too much (infra/app.bicep).
///
/// One file per (table, chunk) is what a bulk loader wants, since SqlBulkCopy targets one destination
/// table per call, and it keeps any single file small enough to open and eyeball.
/// </summary>
public sealed class TableWriter : IDisposable
{
    private readonly string _dir;
    private readonly string _table;
    private readonly int _rowsPerChunk;

    private GZipStream? _gzip;
    private StreamWriter? _writer;
    private int _chunk;
    private int _rowsInChunk;

    public long RowCount { get; private set; }
    public long ByteCount { get; private set; }

    public TableWriter(string outDir, string table, int rowsPerChunk)
    {
        _table = table;
        _rowsPerChunk = rowsPerChunk;
        _dir = Path.Combine(outDir, table);
        Directory.CreateDirectory(_dir);
    }

    public void Write(IReadOnlyDictionary<string, object?> row)
    {
        if (_writer is null || _rowsInChunk >= _rowsPerChunk) Roll();

        // No options, matching how the product serializes audit JSON: JsonSerializerOptions.Default.
        var json = JsonSerializer.Serialize(row);
        _writer!.WriteLine(json);

        RowCount++;
        _rowsInChunk++;
        ByteCount += Encoding.UTF8.GetByteCount(json) + 1;
    }

    private void Roll()
    {
        CloseChunk();
        _chunk++;
        // Re-assert the directory rather than trusting the one made in the constructor. A long run can
        // outlive whatever else is touching the output path, and losing a multi-hour generation to a
        // missing directory on chunk four is not a trade worth making for one saved syscall.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{_table}-{_chunk:D5}.jsonl.gz");
        var file = File.Create(path);
        _gzip = new GZipStream(file, CompressionLevel.Optimal);
        _writer = new StreamWriter(_gzip, new UTF8Encoding(false));
        _rowsInChunk = 0;
    }

    private void CloseChunk()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _gzip?.Dispose();
        _writer = null;
        _gzip = null;
    }

    public void Dispose() => CloseChunk();
}

/// <summary>Owns every open table writer for one run and reports their totals.</summary>
public sealed class ArtifactWriter : IDisposable
{
    private readonly string _outDir;
    private readonly int _rowsPerChunk;
    private readonly Dictionary<string, TableWriter> _writers = new(StringComparer.Ordinal);

    public ArtifactWriter(string outDir, int rowsPerChunk)
    {
        _outDir = outDir;
        _rowsPerChunk = rowsPerChunk;
        Directory.CreateDirectory(outDir);
    }

    public void Write(string table, IReadOnlyDictionary<string, object?> row)
    {
        if (!_writers.TryGetValue(table, out var w))
        {
            w = new TableWriter(_outDir, table, _rowsPerChunk);
            _writers[table] = w;
        }
        w.Write(row);
    }

    public IReadOnlyDictionary<string, (long Rows, long Bytes)> Totals =>
        _writers.ToDictionary(kv => kv.Key, kv => (kv.Value.RowCount, kv.Value.ByteCount), StringComparer.Ordinal);

    public long TotalRows => _writers.Values.Sum(w => w.RowCount);

    public void Dispose()
    {
        foreach (var w in _writers.Values) w.Dispose();
    }
}
