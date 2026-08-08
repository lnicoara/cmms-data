using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cmms.LoadDataRunner.SqlTests;

// lnicoara/cmms#2979: the clear has to be able to FINISH.
//
// The defect these pin: ClearTableAsync issued one unbounded `DELETE FROM [t]` under a fixed
// CommandTimeout. A table's duration grows with its row count and the budget does not, so on pre-prod's
// demo-health.AuditEvents the statement spent the whole 600s, hit the timeout, and rolled back. Nothing
// was removed, so the next attempt did exactly the same thing. The clear was not slow, it was incapable
// of completing, and three consecutive production runs failed at precisely 600 seconds before anyone read
// the logs closely enough to see the load had never started.
//
// A row-count assertion alone would not have caught it: the old code emptied a small table correctly and
// the tests all used small tables. What has to be asserted is that the work is DIVIDED, because that is
// the property that makes completion independent of size.
[Trait("Category", "SqlServer")]
[Collection("SqlSerial")]   // lnicoara/cmms#2148: only one SQL Edge container at a time
public sealed class TargetCleanerBatchingSqlTests : IClassFixture<LoadSqlEdgeFixture>
{
    private readonly LoadSqlEdgeFixture _fx;

    public TargetCleanerBatchingSqlTests(LoadSqlEdgeFixture fx) => _fx = fx;

    private string Conn()
    {
        using var db = _fx.NewContext();
        return db.Database.GetConnectionString()!;
    }

    private async Task<long> CountAsync(string table)
    {
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(*) FROM [{table}]";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(Conn());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // A standalone table nothing references, created here rather than borrowed from the model, so the test
    // owns its own row counts and cannot be perturbed by another suite's data.
    private async Task CreateAndFillAsync(string table, int rows)
    {
        await ExecAsync($"IF OBJECT_ID('{table}') IS NOT NULL DROP TABLE [{table}]");
        await ExecAsync($"CREATE TABLE [{table}] (Id int NOT NULL PRIMARY KEY, Filler nvarchar(64) NULL)");
        await ExecAsync(
            $"WITH n AS (SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i " +
            "FROM sys.all_objects a CROSS JOIN sys.all_objects b) " +
            $"INSERT INTO [{table}] (Id, Filler) SELECT i, 'x' FROM n");
    }

    /// <summary>
    /// The batching itself, on the path a real table takes.
    ///
    /// A foreign key pointing at the table is what forces the DELETE fallback, and it is not contrived:
    /// every table in the artifact's write set is referenced by something, which is why the original code
    /// chose DELETE over TRUNCATE in the first place. Without the child table here this test silently
    /// truncated and asserted nothing about batching at all.
    ///
    /// The fixture holds more rows than one batch can take, so a single-statement clear and a batched one
    /// are distinguishable: only the batched one reports progress. The size is read from the constant
    /// rather than written as a literal, so retuning the constant does not break the test.
    /// </summary>
    [SkippableFact]
    public async Task Clear_deletes_in_batches_rather_than_one_statement()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        // Two batches' worth plus a remainder, so the loop runs at least three times and then sees zero.
        var rows = (TargetDatabase.ClearBatchRows * 2) + 7;
        await ExecAsync("IF OBJECT_ID('ClearBatchChild') IS NOT NULL DROP TABLE [ClearBatchChild]");
        await CreateAndFillAsync("ClearBatchProbe", rows);
        await ExecAsync(
            "CREATE TABLE [ClearBatchChild] (Id int NOT NULL PRIMARY KEY, " +
            "ParentId int NOT NULL REFERENCES [ClearBatchProbe](Id))");

        var progress = new List<string>();
        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);
        var deleted = await target.ClearTableAsync("ClearBatchProbe", CancellationToken.None, progress.Add);

        Assert.Equal(rows, deleted);
        Assert.Equal(0, await CountAsync("ClearBatchProbe"));

        // The point of the change. One statement reports nothing; a divided one reports as it goes.
        Assert.NotEmpty(progress);
        Assert.All(progress, line => Assert.Contains("ClearBatchProbe", line));

        await ExecAsync("DROP TABLE [ClearBatchChild]");
        await ExecAsync("DROP TABLE [ClearBatchProbe]");
    }

    /// <summary>
    /// A table a foreign key REFERENCES cannot be truncated, even when the referencing table is empty,
    /// so the fallback is the path real tables take. It still has to empty them.
    /// </summary>
    [SkippableFact]
    public async Task Clear_empties_a_table_a_foreign_key_points_at()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        await ExecAsync("IF OBJECT_ID('ClearFkChild') IS NOT NULL DROP TABLE [ClearFkChild]");
        await CreateAndFillAsync("ClearFkParent", 1_000);
        await ExecAsync(
            "CREATE TABLE [ClearFkChild] (Id int NOT NULL PRIMARY KEY, " +
            "ParentId int NOT NULL REFERENCES [ClearFkParent](Id))");

        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);
        var deleted = await target.ClearTableAsync("ClearFkParent", CancellationToken.None);

        Assert.Equal(1_000, deleted);
        Assert.Equal(0, await CountAsync("ClearFkParent"));

        await ExecAsync("DROP TABLE [ClearFkChild]");
        await ExecAsync("DROP TABLE [ClearFkParent]");
    }

    /// <summary>
    /// The unreferenced case, which is what AuditEvents is: nothing points at it, so TRUNCATE is available
    /// and the clear costs the same at five thousand rows as at forty million.
    ///
    /// The row count is the assertion that matters here. TRUNCATE reports nothing back, and the first cut
    /// of this returned the count taken AFTERWARDS, so it reported "0 rows deleted" for a table that had
    /// just given up every row it held. An operator reading that line has no way to tell a table that was
    /// emptied from one that was skipped.
    /// </summary>
    [SkippableFact]
    public async Task Clear_of_an_unreferenced_table_reports_what_it_actually_removed()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        const string Table = "ClearUnreferencedProbe";
        await CreateAndFillAsync(Table, 5_000);

        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);
        var deleted = await target.ClearTableAsync(Table, CancellationToken.None);

        Assert.Equal(5_000, deleted);
        Assert.Equal(0, await CountAsync(Table));

        await ExecAsync($"DROP TABLE [{Table}]");
    }

    /// <summary>
    /// Clearing an already-empty table is the overwhelmingly common case: 134 of pre-prod's 136 tables were
    /// empty and went through in one second. It must stay cheap and must report zero rather than throw.
    /// </summary>
    [SkippableFact]
    public async Task Clear_of_an_empty_table_removes_nothing_and_reports_nothing()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        const string Table = "ClearEmptyProbe";
        await CreateAndFillAsync(Table, 0);

        var progress = new List<string>();
        var target = new TargetDatabase(Conn(), timeoutSeconds: 120);
        var deleted = await target.ClearTableAsync(Table, CancellationToken.None, progress.Add);

        Assert.Equal(0, deleted);
        Assert.Empty(progress);

        await ExecAsync($"DROP TABLE [{Table}]");
    }
}
