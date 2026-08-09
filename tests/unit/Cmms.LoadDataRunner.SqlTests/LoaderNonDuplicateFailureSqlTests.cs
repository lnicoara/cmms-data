using System.IO.Compression;
using System.Text.Json;
using Cmms.LoadDataGenerator;
using Cmms.LoadDataRunner;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cmms.LoadDataRunner.SqlTests;

/// <summary>
/// The half of lnicoara/cmms#3054 that proves the duplicate-key catch is NARROW: a bulk-copy failure which
/// is not a duplicate key still ends the load. A catch that swallowed everything would turn a corrupt load
/// into a silent one, which is worse than the failure it was added to survive.
///
/// ITS OWN CLASS, and therefore its own container and database. The only way to make the loader fail for a
/// reason that is not a duplicate is to break the schema, and doing that in the shared class poisoned the
/// database for every test that ran afterwards: five of them failed on a column this test had dropped.
/// IClassFixture gives each class its own database (#2148), which is exactly the isolation a destructive
/// test needs, and [Collection("SqlSerial")] still keeps only one container alive at a time.
/// </summary>
[Trait("Category", "SqlServer")]
[Collection("SqlSerial")]
public sealed class LoaderNonDuplicateFailureSqlTests : IClassFixture<LoadSqlEdgeFixture>
{
    private readonly LoadSqlEdgeFixture _fx;
    public LoaderNonDuplicateFailureSqlTests(LoadSqlEdgeFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task A_failure_that_is_not_a_duplicate_key_still_stops_the_load()
    {
        Skip.IfNot(_fx.Available, _fx.SkipReason);

        var dir = LoadSqlFixtures.BuildArtifact(nameof(LoaderNonDuplicateFailureSqlTests));

        // Drop a column the artifact carries. The bulk copy then fails on a column mismatch, which is
        // nothing like a duplicate key and must not be mistaken for "this work is already done".
        await using (var conn = new SqlConnection(_fx.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE [WorkOrders] DROP COLUMN [Description]";
            await cmd.ExecuteNonQueryAsync();
        }

        // It THROWS rather than returning a failed report, and that is the point: anything which is not a
        // duplicate key propagates exactly as it did before the catch was added.
        await Assert.ThrowsAnyAsync<Exception>(
            () => LoadSqlFixtures.RunAsync(_fx, dir, execute: true));
    }
}
