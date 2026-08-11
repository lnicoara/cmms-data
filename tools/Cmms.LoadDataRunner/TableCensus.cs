using Microsoft.Data.SqlClient;

namespace Cmms.LoadDataRunner;

/// <summary>
/// Every table in a tenant database and how many rows it holds.
///
/// It exists because pre-prod SQL sets Deny Public Network Access, so the obvious way to answer "what is
/// actually in there" — connect and run a query — is refused at login from anywhere outside the VNet:
///   mssql: login error: Connection was denied because Deny Public Network Access is set to Yes.
/// The count therefore has to run in a container that is already inside, which is why it lives in this
/// binary rather than in the operator script that asks for it.
///
/// ONE query, not one per table. The loader's per-table <see cref="TargetDatabase.RowCountAsync"/> is the
/// right shape for preflight, which asks about a handful of tables it is about to write; asking it 158
/// times would be 158 round trips to answer a question the engine can answer in one.
///
/// Counted from sys.partitions rather than COUNT(*), and that is the difference between a report and an
/// outage. COUNT(*) on AuditEvents scans 34 million rows on a General Purpose database, under whatever
/// command timeout the caller happened to set, while a load is writing to that same table. The partition
/// metadata is engine-maintained and answers in constant time no matter how large the table is.
///
/// The cost of that choice, stated plainly: for a table with rows in flight the number is approximate,
/// because the metadata is updated by the engine rather than transactionally. On an idle database it is
/// exact. During a load it can lag the truth by a chunk, which for "how far has this got" is precision
/// nobody needs and a table scan is a bad way to buy.
/// </summary>
public static class TableCensus
{
    /// <summary>One table's name and row count.</summary>
    public readonly record struct TableRows(string Schema, string Table, long Rows);

    // index_id 0 is a heap and 1 a clustered index; every other id is a nonclustered copy of the same rows
    // and including them would multiply the count by the number of indexes. is_ms_shipped excludes the
    // engine's own objects. The ORDER BY is here rather than in the caller so the transport order is
    // already the report order, and a truncated read is still alphabetical as far as it got.
    private const string Sql = """
        SELECT s.name AS SchemaName,
               t.name AS TableName,
               ISNULL(SUM(p.rows), 0) AS RowCount
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name
        ORDER BY t.name
        """;

    /// <summary>
    /// Reads the census. Returns every user table, including ones no artifact writes and ones holding
    /// nothing: a table absent from the output would be indistinguishable from a table at zero, and "which
    /// tables are empty" is most of what the question is for.
    /// </summary>
    public static async Task<IReadOnlyList<TableRows>> ReadAsync(
        string connectionString, int timeoutSeconds, CancellationToken ct)
    {
        var rows = new List<TableRows>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = timeoutSeconds;
        cmd.CommandText = Sql;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new TableRows(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

        return rows;
    }

    /// <summary>
    /// Writes the census to stdout as one MACHINE-READABLE line per table, plus a human summary.
    ///
    /// The tab-delimited COUNT lines are the contract with count-records.sh, which does the column
    /// alignment. The formatting deliberately does NOT happen here: this output reaches the operator
    /// through `az containerapp job logs show`, which returns each line as a separate JSON record and may
    /// return them out of order, so anything that relied on adjacent lines lining up on screen would be
    /// laid out by the container and then scrambled in transit. A prefix and a delimiter survive that;
    /// a padded column does not.
    /// </summary>
    public static void Write(string database, IReadOnlyList<TableRows> census)
    {
        Console.WriteLine($"CENSUS-DATABASE\t{database}");
        foreach (var r in census)
            Console.WriteLine($"COUNT\t{r.Schema}\t{r.Table}\t{r.Rows}");
        Console.WriteLine($"CENSUS-END\t{census.Count}\t{census.Sum(r => r.Rows)}");
    }
}
