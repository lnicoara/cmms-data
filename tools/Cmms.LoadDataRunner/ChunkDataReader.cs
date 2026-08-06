using System.Data;
using System.IO.Compression;
using System.Text.Json;

namespace Cmms.LoadDataRunner;

/// <summary>
/// An <see cref="IDataReader"/> over one gzipped JSON Lines chunk file.
///
/// Streaming by construction, which is the whole point: read a line, parse it, hand its values to
/// SqlBulkCopy, forget it. Nothing accumulates, so a 100,000-row chunk and a 100-row chunk cost the same
/// retained memory. SqlBulkCopy must be configured with EnableStreaming or it buffers the source anyway
/// and undoes this.
///
/// Only the members SqlBulkCopy actually uses are implemented. The rest of IDataReader throws, because a
/// half-working reader that silently returns a wrong value is worse than one that stops.
/// </summary>
public sealed class ChunkDataReader : IDataReader
{
    private readonly IReadOnlyList<ColumnBinder> _binders;
    private readonly Dictionary<string, int> _ordinals;
    private readonly StreamReader _reader;
    private readonly GZipStream _gzip;
    private readonly FileStream _file;
    private readonly string _path;

    private JsonDocument? _current;
    private object?[] _values;
    private long _rowNumber;

    /// <summary>
    /// Rows actually handed to the consumer. The caller checks this against the count the artifact's v1
    /// contract predicted, which is what turns that contract from an assumption into a verified fact.
    /// </summary>
    public long RowsRead => _rowNumber;

    public ChunkDataReader(string path, IReadOnlyList<ColumnBinder> binders)
    {
        _path = path;
        _binders = binders;
        _ordinals = new Dictionary<string, int>(binders.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < binders.Count; i++) _ordinals[binders[i].ColumnName] = i;
        _values = new object?[binders.Count];

        _file = File.OpenRead(path);
        _gzip = new GZipStream(_file, CompressionMode.Decompress);
        _reader = new StreamReader(_gzip);
    }

    public bool Read()
    {
        var line = _reader.ReadLine();
        if (line is null) return false;

        _rowNumber++;
        _current?.Dispose();
        try
        {
            _current = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{_path} row {_rowNumber}: malformed JSON. {ex.Message}", ex);
        }

        var root = _current.RootElement;
        for (var i = 0; i < _binders.Count; i++)
        {
            var b = _binders[i];
            // A missing column is corruption, not a null. The generator writes every column of its manifest
            // on every row, so absence means the artifact and the model disagree, and coercing it to null
            // would quietly insert a wrong row instead of stopping.
            if (!root.TryGetProperty(b.ColumnName, out var element))
                throw new InvalidOperationException(
                    $"{_path} row {_rowNumber}: no value for column '{b.ColumnName}'. " +
                    "The artifact does not match the model it is being loaded against.");
            try
            {
                _values[i] = b.Read(element);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
            {
                throw new InvalidOperationException(
                    $"{_path} row {_rowNumber}: column '{b.ColumnName}' holds {element} which is not a " +
                    $"valid {b.FieldType.Name}. {ex.Message}", ex);
            }
        }
        return true;
    }

    public int FieldCount => _binders.Count;
    public string GetName(int i) => _binders[i].ColumnName;
    public Type GetFieldType(int i) => _binders[i].FieldType;
    public object GetValue(int i) => _values[i] ?? DBNull.Value;
    public bool IsDBNull(int i) => _values[i] is null;

    public int GetOrdinal(string name) =>
        _ordinals.TryGetValue(name, out var i)
            ? i
            : throw new IndexOutOfRangeException($"No column '{name}' in {_path}.");

    public void Close() => Dispose();
    public bool IsClosed { get; private set; }

    public void Dispose()
    {
        if (IsClosed) return;
        IsClosed = true;
        _current?.Dispose();
        _reader.Dispose();
        _gzip.Dispose();
        _file.Dispose();
    }

    // Everything below is IDataReader surface SqlBulkCopy does not use on this path. Throwing beats
    // returning a plausible wrong answer.
    public int Depth => 0;
    public int RecordsAffected => -1;
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));
    public bool NextResult() => false;
    public DataTable? GetSchemaTable() => null;
    public int GetValues(object[] values) => throw new NotSupportedException();
    public bool GetBoolean(int i) => throw new NotSupportedException();
    public byte GetByte(int i) => throw new NotSupportedException();
    public long GetBytes(int i, long o, byte[]? buf, int bo, int len) => throw new NotSupportedException();
    public char GetChar(int i) => throw new NotSupportedException();
    public long GetChars(int i, long o, char[]? buf, int bo, int len) => throw new NotSupportedException();
    public IDataReader GetData(int i) => throw new NotSupportedException();
    public string GetDataTypeName(int i) => throw new NotSupportedException();
    public DateTime GetDateTime(int i) => throw new NotSupportedException();
    public decimal GetDecimal(int i) => throw new NotSupportedException();
    public double GetDouble(int i) => throw new NotSupportedException();
    public float GetFloat(int i) => throw new NotSupportedException();
    public Guid GetGuid(int i) => throw new NotSupportedException();
    public short GetInt16(int i) => throw new NotSupportedException();
    public int GetInt32(int i) => throw new NotSupportedException();
    public long GetInt64(int i) => throw new NotSupportedException();
    public string GetString(int i) => throw new NotSupportedException();
}
