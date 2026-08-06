using System.Data;
using Microsoft.Data.SqlClient;

namespace Cmms.LoadDataRunner;

/// <summary>
/// The read-side questions the loader asks its target, and the bulk-copy it performs.
///
/// Every question here is answered without a table scan where that is possible, because preflight runs on
/// every restart of a load that is already measured in hours.
/// </summary>
public sealed class TargetDatabase
{
    private readonly string _connectionString;
    private readonly int _timeoutSeconds;

    public TargetDatabase(string connectionString, int timeoutSeconds)
    {
        _connectionString = connectionString;
        _timeoutSeconds = timeoutSeconds;
    }

    public string DatabaseName => new SqlConnectionStringBuilder(_connectionString).InitialCatalog;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqlConnection(_connectionString);
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>
    /// Whether the table holds any row at all. This is the SAFETY-critical check, because it is what stops
    /// a fresh load landing in a database that already has data, and it is also the cheap one: TOP 1 is a
    /// single indexed seek whose cost does not grow with the table.
    /// </summary>
    public async Task<bool> HasAnyRowAsync(string table, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.CommandText = $"SELECT TOP 1 1 FROM {Quote(table)}";
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Exact row count without scanning. sys.dm_db_partition_stats is engine-maintained metadata for the
    /// heap or clustered index, so this answers in constant time on a 34-million-row table. It needs
    /// VIEW DATABASE STATE, a READ permission, so it stays inside the least-privilege posture; when that
    /// is unavailable the count falls back to COUNT_BIG rather than being skipped, because a check that
    /// silently degrades is worse than a slow one.
    /// </summary>
    public async Task<long> RowCountAsync(string table, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        try
        {
            await using var fast = conn.CreateCommand();
            fast.CommandTimeout = _timeoutSeconds;
            fast.CommandText =
                "SELECT SUM(ps.row_count) FROM sys.dm_db_partition_stats ps " +
                "WHERE ps.object_id = OBJECT_ID(@t) AND ps.index_id IN (0, 1)";
            fast.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 256) { Value = table });
            var result = await fast.ExecuteScalarAsync(ct);
            if (result is not null && result != DBNull.Value) return Convert.ToInt64(result);
        }
        catch (SqlException)
        {
            // No VIEW DATABASE STATE. Fall through and pay for the scan.
        }

        await using var slow = conn.CreateCommand();
        slow.CommandTimeout = _timeoutSeconds;
        slow.CommandText = $"SELECT COUNT_BIG(*) FROM {Quote(table)}";
        return Convert.ToInt64(await slow.ExecuteScalarAsync(ct));
    }

    /// <summary>Whether one specific primary key is present. Used to probe a chunk's first and last row.</summary>
    public async Task<bool> KeyExistsAsync(
        string table, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> keyValues, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _timeoutSeconds;
        var where = string.Join(" AND ", keyColumns.Select((c, i) => $"{Quote(c)} = @p{i}"));
        cmd.CommandText = $"SELECT TOP 1 1 FROM {Quote(table)} WHERE {where}";
        for (var i = 0; i < keyValues.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", keyValues[i] ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Deletes every row from one table and returns how many went. DELETE rather than TRUNCATE because
    /// TRUNCATE is refused on a table referenced by a foreign key, and these tables reference each other;
    /// the caller works in reverse dependency order so children are gone before their parents.
    ///
    /// Only reachable behind BOTH --clear-target and --execute. Nothing on the load path calls it.
    /// </summary>
    public async Task<long> ClearTableAsync(string table, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _timeoutSeconds;
        cmd.CommandText = $"DELETE FROM {Quote(table)}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Bulk-copies one chunk inside ONE explicit transaction, so the chunk is all or nothing (grill round
    /// 2). BatchSize below only bounds how much is in flight; it cannot split the commit, which is what
    /// makes the checkpoint a reliable statement about what landed.
    /// </summary>
    public async Task CopyChunkAsync(
        string table, IReadOnlyList<ColumnBinder> binders, string chunkPath, long expectedRows,
        int batchSize, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            // SqlBulkCopyOptions.Default is chosen, not inherited: it does NOT check foreign keys or check
            // constraints, which is what makes 42.7M rows affordable. Relational validity is the artifact's
            // property (the generator emits a closed reference graph) and re-establishing the optimizer's
            // trust in it afterward belongs to the operator script, alongside the index rebuild.
            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = Quote(table),
                BatchSize = batchSize,
                BulkCopyTimeout = _timeoutSeconds,
                // Without this the reader is drained into memory before anything is sent, which would undo
                // the streaming the whole artifact format is built around.
                EnableStreaming = true,
            };
            foreach (var b in binders)
                bulk.ColumnMappings.Add(b.ColumnName, b.ColumnName);

            using var reader = new ChunkDataReader(chunkPath, binders);
            await bulk.WriteToServerAsync(reader, ct);

            // The v1 contract says a non-final chunk holds exactly rowsPerChunk rows, and the whole
            // count derivation trusts that without ever measuring it. Measure it HERE, where the rows
            // have just been read anyway, so the assumption cannot silently be false. A truncated or
            // appended chunk would otherwise commit short, get checkpointed, and be reported at its
            // assumed count: a wrong dataset that every subsequent check agrees with.
            //
            // Inside the transaction on purpose. Throwing here rolls the chunk back, so a bad chunk
            // leaves nothing behind rather than a partial load to reconcile.
            if (reader.RowsRead != expectedRows)
                throw new InvalidOperationException(
                    $"{chunkPath} holds {reader.RowsRead:N0} rows but the artifact accounts for it as " +
                    $"{expectedRows:N0}. The chunk does not match its manifest, so the load is rolled back.");

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Identifiers come from the EF model rather than from user input, but bracket-quoting them costs
    /// nothing and a name carrying a bracket should stop the run rather than change the statement.
    /// </summary>
    private static string Quote(string identifier)
    {
        if (identifier.Contains('[') || identifier.Contains(']'))
            throw new InvalidOperationException($"Refusing to build SQL for identifier '{identifier}'.");
        return $"[{identifier}]";
    }
}
