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
    /// Runs one database operation, retrying it when Azure SQL drops it for a reason that is going to pass.
    ///
    /// This class opened raw SqlConnections with no resiliency at all, while the EF paths in the same
    /// process used UseResilientSqlServer. Azure SQL moves databases between nodes for its own reasons and
    /// closes connections when it does, and on a load measured in hours the odds of meeting one are not
    /// small. Without this, a blip that would cost a second instead cost the whole run.
    ///
    /// The operation must be RE-RUNNABLE, which is why the retry lives here rather than around a command:
    /// each attempt opens its own connection, because a connection killed by a failover cannot be reused.
    /// Every caller here satisfies that. The reads are idempotent, a DELETE batch that failed either
    /// committed or rolled back whole, and a chunk copy is wrapped in its own transaction precisely so a
    /// retry repeats it rather than doubling it.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<SqlConnection, Task<T>> work, CancellationToken ct)
    {
        const int MaxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await OpenAsync(ct);
                return await work(conn);
            }
            catch (SqlException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                // Backs off 2s, 4s, 8s, 16s. A node failover takes seconds to tens of seconds, so retrying
                // immediately would spend every attempt inside the same outage and report a failure that
                // waiting would have avoided.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.WriteLine(
                    $"    transient SQL error {FirstErrorNumber(ex)} on attempt {attempt} of {MaxAttempts}; " +
                    $"retrying in {delay.TotalSeconds:N0}s: {ex.Message.Split('\n')[0]}");
                await Task.Delay(delay, ct);
            }
        }
    }

    private static int FirstErrorNumber(SqlException ex) =>
        ex.Errors.Count > 0 ? ex.Errors[0].Number : ex.Number;

    /// <summary>
    /// Whether the failure is one that retrying can clear.
    ///
    /// Deliberately a LIST rather than "anything that is not a constraint violation". A retry on a genuine
    /// error turns a clear failure into the same failure five times over, several minutes later, which is
    /// worse than failing at once. -2, the command timeout, is included because a batch is now bounded, so
    /// a timeout means the server was busy rather than that the work cannot fit.
    /// </summary>
    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is
            -2 or          // command timeout
            20 or 64 or    // transport-level errors on send/receive
            233 or         // no process on the other end of the pipe
            1205 or        // deadlock victim
            4060 or        // cannot open the database right now
            10053 or 10054 or 10060 or  // transport aborted, reset by peer, connect timeout
            10928 or 10929 or           // resource limits reached
            40197 or 40501 or 40613 or  // service error, service busy, database unavailable
            49918 or 49919 or 49920 or  // cannot process, too many operations, too busy
            11001);        // host not resolved during a DNS blip

    /// <summary>
    /// Whether the table holds any row at all. This is the SAFETY-critical check, because it is what stops
    /// a fresh load landing in a database that already has data, and it is also the cheap one: TOP 1 is a
    /// single indexed seek whose cost does not grow with the table.
    /// </summary>
    public Task<bool> HasAnyRowAsync(string table, CancellationToken ct) =>
        WithRetryAsync(conn => HasAnyRowAsync(conn, table, ct), ct);

    // Same question on a caller's connection, so the clear can ask it without a second round of connect
    // and teardown per table.
    private async Task<bool> HasAnyRowAsync(SqlConnection conn, string table, CancellationToken ct)
    {
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
    public Task<long> RowCountAsync(string table, CancellationToken ct) =>
        WithRetryAsync(async conn =>
        {
            var fromMetadata = await TryCountFromMetadataAsync(conn, table, ct);
            if (fromMetadata >= 0) return fromMetadata;

            await using var slow = conn.CreateCommand();
            slow.CommandTimeout = _timeoutSeconds;
            slow.CommandText = $"SELECT COUNT_BIG(*) FROM {Quote(table)}";
            return Convert.ToInt64(await slow.ExecuteScalarAsync(ct));
        }, ct);

    /// <summary>
    /// The row count read from engine metadata, or -1 when that is unavailable.
    ///
    /// Separated out and made to report its own absence, because the two callers want opposite things from
    /// a failure. RowCountAsync wants the exact number and will pay for a scan. The clear only wants a
    /// number to report, and paying for a scan there would put an unbounded operation under a fixed
    /// timeout on the largest table in the database, which is the precise hazard the clear was rewritten
    /// to remove.
    /// </summary>
    private async Task<long> TryCountFromMetadataAsync(SqlConnection conn, string table, CancellationToken ct)
    {
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
        catch (SqlException ex) when (IsPermissionDenied(ex))
        {
            // No VIEW DATABASE STATE.
            //
            // Narrowed to the permission case this was written for. A bare catch(SqlException) also
            // swallowed error -2, the command timeout, and then started a COUNT_BIG scan with a fresh
            // full budget on a connection the timeout may already have broken. One call could spend
            // twice the timeout and surface as a failure of the scan, hiding that the metadata read
            // was the thing that ran out of time.
        }
        return -1;
    }

    /// <summary>Whether one specific primary key is present. Used to probe a chunk's first and last row.</summary>
    public Task<bool> KeyExistsAsync(
        string table, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> keyValues, CancellationToken ct) =>
        WithRetryAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _timeoutSeconds;
            var where = string.Join(" AND ", keyColumns.Select((c, i) => $"{Quote(c)} = @p{i}"));
            cmd.CommandText = $"SELECT TOP 1 1 FROM {Quote(table)} WHERE {where}";
            for (var i = 0; i < keyValues.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", keyValues[i] ?? DBNull.Value);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }, ct);

    /// <summary>
    /// How many rows one DELETE batch removes.
    ///
    /// 5,000 rather than something larger, for two reasons that both argue the same way. SQL Server
    /// escalates row locks to a TABLE lock at around 5,000 locks in a single statement, so a bigger batch
    /// escalates on every pass and holds the whole table while it runs. And on a General Purpose tier,
    /// whose log throughput is throttled near 4.5 MB/s, a batch is a burst that has to be flushed before
    /// it commits, so a bigger one delays the commit that makes the work durable.
    ///
    /// Bigger batches buy fewer round trips, which is the cheapest of the three things being traded.
    /// </summary>
    public const int ClearBatchRows = 5_000;

    /// <summary>
    /// Blanks the named nullable foreign-key columns on a table, so a later DELETE cannot trip over them.
    ///
    /// The clear walks tables in reverse dependency order, which is sound for REQUIRED foreign keys because
    /// those are acyclic. Optional ones are not: ServiceLine.SupervisorId references Labor while
    /// Labor.ServiceLineId references ServiceLines, a genuine two-table cycle that loads fine (a nullable
    /// key needs no parent at insert) and that no delete order can satisfy, because DELETE checks the
    /// constraint whatever the nullability. Emptying the columns first removes the edge instead of trying
    /// to order around it. Harmless by construction: every row here is about to be deleted anyway.
    /// lnicoara/cmms#2993.
    /// </summary>
    public Task<int> NullifyOptionalLinksAsync(
        string table, IReadOnlyList<string> columns, CancellationToken ct) =>
        WithRetryAsync(async conn =>
        {
            if (columns.Count == 0) return 0;
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.CommandText =
                $"UPDATE {Quote(table)} SET " +
                string.Join(", ", columns.Select(c => $"{Quote(c)} = NULL")) +
                " WHERE " + string.Join(" OR ", columns.Select(c => $"{Quote(c)} IS NOT NULL"));
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>
    /// Empties one table and returns how many rows went.
    ///
    /// TRUNCATE first, because it is minimally logged and so costs the same at two rows as at forty
    /// million. It is refused on a table a foreign key REFERENCES (regardless of whether the referencing
    /// table holds rows, so the caller's reverse dependency order does not earn it) and refused without
    /// ALTER permission, which this runner deliberately does not hold. Both refusals fall back.
    ///
    /// The fallback deletes in batches rather than in one statement. A single unbounded DELETE was the
    /// defect: its duration grows with the table while its budget does not, so on demo-health's
    /// AuditEvents it spent the full 600s, hit CommandTimeout, and rolled back. Every retry repeated it
    /// exactly, removing nothing, which made the clear permanently unable to finish rather than merely
    /// slow. Batches commit as they go, so the work is durable, the log truncates behind it, and a run
    /// that is interrupted leaves the next one less to do.
    ///
    /// Only reachable behind BOTH --clear-target and --execute. Nothing on the load path calls it.
    /// </summary>
    /// <param name="progress">
    /// Called after each batch once a table needs more than one, so a long clear reports movement. A
    /// silent ten minutes and a hung ten minutes look identical from outside.
    /// </param>
    /// <summary>
    /// The users this clear must not delete, and the access groups that give them their rights.
    ///
    /// Looked up by username before anything is deleted, because after the clear there is nothing left to
    /// ask. Returns empty when none of the names are present, which is the ordinary case for an unseeded
    /// load-test tenant and is not an error.
    /// </summary>
    public Task<IReadOnlyList<(Guid UserId, Guid? AccessGroupId)>> FindUsersByNameAsync(
        IReadOnlyList<string> usernames, CancellationToken ct) =>
        WithRetryAsync<IReadOnlyList<(Guid, Guid?)>>(async conn =>
        {
            var found = new List<(Guid, Guid?)>();
            if (usernames.Count == 0) return found;

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _timeoutSeconds;
            var names = string.Join(", ", usernames.Select((_, i) => $"@u{i}"));
            cmd.CommandText = $"SELECT [Id], [DefaultAccessGroupId] FROM [Users] WHERE [Username] IN ({names})";
            for (var i = 0; i < usernames.Count; i++)
                cmd.Parameters.Add(new SqlParameter($"@u{i}", SqlDbType.NVarChar, 256) { Value = usernames[i] });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                found.Add((reader.GetGuid(0), await reader.IsDBNullAsync(1, ct) ? null : reader.GetGuid(1)));
            return found;
        }, ct);


    /// <summary>
    /// Every column of the named rows, read out before they are deleted.
    ///
    /// The rows are CAPTURED and put back after the load rather than spared by the clear, because the
    /// loader's own preflight refuses a target holding rows the artifact does not account for
    /// ("REFUSING: Users holds 1 rows ... The extra rows did not come from this artifact"). That refusal
    /// is worth keeping: it is what stops a load landing on top of somebody's real data. So the account
    /// steps out of the way for the duration rather than the check being weakened around it.
    /// </summary>
    public Task<IReadOnlyList<Dictionary<string, object?>>> CaptureRowsAsync(
        string table, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        WithRetryAsync<IReadOnlyList<Dictionary<string, object?>>>(async conn =>
        {
            var rows = new List<Dictionary<string, object?>>();
            if (ids.Count == 0) return rows;

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _timeoutSeconds;
            var ps = string.Join(", ", ids.Select((_, i) => $"@i{i}"));
            cmd.CommandText = $"SELECT * FROM {Quote(table)} WHERE [Id] IN ({ps})";
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.Add(new SqlParameter($"@i{i}", SqlDbType.UniqueIdentifier) { Value = ids[i] });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var c = 0; c < reader.FieldCount; c++)
                    row[reader.GetName(c)] = await reader.IsDBNullAsync(c, ct) ? null : reader.GetValue(c);
                rows.Add(row);
            }
            return rows;
        }, ct);

    /// <summary>
    /// Writes captured rows back, column for column. Skips any whose key is already present, so restoring
    /// twice is harmless and a row the artifact happened to supply is never overwritten.
    /// </summary>
    public Task<int> RestoreRowsAsync(
        string table, IReadOnlyList<Dictionary<string, object?>> rows, CancellationToken ct) =>
        WithRetryAsync(async conn =>
        {
            var written = 0;
            foreach (var row in rows)
            {
                var columns = row.Keys.ToList();
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _timeoutSeconds;
                cmd.CommandText =
                    $"INSERT INTO {Quote(table)} ({string.Join(", ", columns.Select(Quote))}) " +
                    $"SELECT {string.Join(", ", columns.Select((_, i) => $"@c{i}"))} " +
                    $"WHERE NOT EXISTS (SELECT 1 FROM {Quote(table)} WHERE [Id] = @c{columns.IndexOf("Id")})";
                for (var i = 0; i < columns.Count; i++)
                    cmd.Parameters.AddWithValue($"@c{i}", row[columns[i]] ?? DBNull.Value);
                written += await cmd.ExecuteNonQueryAsync(ct);
            }
            return written;
        }, ct);

    public async Task<long> ClearTableAsync(string table, CancellationToken ct, Action<string>? progress = null)
    {
        // The cheap question first, and it answers for almost every table. 134 of pre-prod's 136 were
        // already empty, and TOP 1 is a single seek whose cost does not grow with the table, so the common
        // case costs one probe instead of a DELETE that scans to find nothing.
        if (!await HasAnyRowAsync(table, ct)) return 0;

        // Counted BEFORE, because TRUNCATE reports no row count and the caller prints this number as a
        // statement about what was removed. Returning the count taken AFTERWARDS said "0 rows deleted" for
        // a table that had just given up a hundred thousand, which is the same species of misleading
        // output that made this defect take three failed production runs to read correctly.
        //
        // Metadata ONLY: -1 when it is unavailable, and then TRUNCATE is skipped rather than paying for a
        // COUNT_BIG scan to name a number. A scan is unbounded work under a fixed timeout on the largest
        // table in the database, which is the hazard this method exists to remove. The batched path counts
        // as it deletes, so declining to truncate costs accuracy nothing.
        var before = await WithRetryAsync(conn => TryCountFromMetadataAsync(conn, table, ct), ct);

        if (before >= 0 && await WithRetryAsync(conn => TryTruncateAsync(conn, table, ct), ct)) return before;

        long total = 0;
        var batches = 0;
        while (true)
        {
            // Each batch gets its own connection through the retry, so a failover between batches costs
            // this batch rather than the clear. Safe because a batch either committed or rolled back
            // whole, and the next DELETE TOP simply takes whatever rows are still there.
            var removed = await WithRetryAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _timeoutSeconds;
                cmd.CommandText = $"DELETE TOP ({ClearBatchRows}) FROM {Quote(table)}";
                return await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            if (removed == 0) break;

            total += removed;
            batches++;
            // Only worth saying once a table is big enough to need more than one pass. Announcing a batch
            // for each of the 134 already-empty tables would bury the one table that is actually working.
            if (batches > 1) progress?.Invoke($"    {table,-28} {total,12:N0} rows deleted so far");
        }
        return total;
    }

    /// <summary>
    /// TRUNCATE the table, or report that it cannot be truncated. A refusal is an ordinary answer here,
    /// not a failure: the caller has a correct fallback. Anything that is NOT a refusal rethrows, because
    /// swallowing it would turn a broken connection or a missing table into a silent slow path.
    /// </summary>
    private async Task<bool> TryTruncateAsync(SqlConnection conn, string table, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = _timeoutSeconds;
            cmd.CommandText = $"TRUNCATE TABLE {Quote(table)}";
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (IsTruncateRefusal(ex))
        {
            return false;
        }
    }

    // 229/230/262/300: SELECT/VIEW permission denied on the object, database, or server state.
    private static bool IsPermissionDenied(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is 229 or 230 or 262 or 300);

    // 4712: cannot truncate because the table is referenced by a FOREIGN KEY constraint.
    // 1088/2812/4701: the object cannot be found or is not a table this statement may target.
    // 229/230/262/300: permission denied, which is the expected answer for a least-privilege principal.
    private static bool IsTruncateRefusal(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e =>
            e.Number is 4712 or 4701 or 1088 or 2812 or 229 or 230 or 262 or 300);

    /// <summary>
    /// Bulk-copies one chunk inside ONE explicit transaction, so the chunk is all or nothing (grill round
    /// 2). BatchSize below only bounds how much is in flight; it cannot split the commit, which is what
    /// makes the checkpoint a reliable statement about what landed.
    /// </summary>
    public Task CopyChunkAsync(
        string table, IReadOnlyList<ColumnBinder> binders, string chunkPath, long expectedRows,
        int batchSize, CancellationToken ct) =>
        // Retried on a transient failure, which the chunk's own transaction is what makes safe: a failed
        // attempt rolled back whole, so a retry repeats the chunk rather than doubling part of it. The
        // checkpoint is written by the caller only after this returns, so a retried chunk is still
        // recorded once.
        WithRetryAsync(conn => CopyChunkOnceAsync(
            conn, table, binders, chunkPath, expectedRows, batchSize, ct), ct);

    private async Task<bool> CopyChunkOnceAsync(
        SqlConnection conn, string table, IReadOnlyList<ColumnBinder> binders, string chunkPath,
        long expectedRows, int batchSize, CancellationToken ct)
    {
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
            return true;
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
