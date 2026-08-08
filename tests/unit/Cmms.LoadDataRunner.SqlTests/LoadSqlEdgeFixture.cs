using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Tenancy;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cmms.LoadDataRunner.SqlTests;

/// <summary>
/// One Azure SQL Edge container (arm64-native) with the full tenant migration set applied, for the half of
/// the loader that can only be proven against a real database. lnicoara/cmms#3026, moved with the tooling.
///
/// It was ApiSqlEdgeFixture in cmms, and it did not move wholesale. That one lives inside a work-order
/// photo test file and carries what a dozen API suites need: interceptor injection, service-line binding, a
/// master connection for provoking transient SqlExceptions. The loader needs three things, so this is those
/// three. Copying the rest would have made a fixture that no test here exercises and that nobody would dare
/// trim later.
///
/// ONLY container start plus login is skip-guarded. A failed CREATE DATABASE or MigrateAsync throws, so a
/// broken schema fails the suite rather than quietly skipping it, which is the difference between "no
/// Docker here" and "the migrations do not apply".
///
/// The migrations come from the PINNED Cmms.Infrastructure package, so this container is running the schema
/// this repository is targeting rather than whatever is checked out next door. That is the same guarantee
/// the artifact has, applied to the thing the artifact is tested against.
/// </summary>
public sealed class LoadSqlEdgeFixture : IAsyncLifetime
{
    private const string Password = "yourStrong(!)Password1";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", Password)
        .WithPortBinding(1433, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1433))
        .Build();

    private string _masterConn = "";
    private string _dbConn = "";

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "Docker is not available for the SQL Server container.";

    /// <summary>The tenant database's connection string, which the loader opens directly.</summary>
    public string ConnectionString => _dbConn;

    public async Task InitializeAsync()
    {
        try
        {
            await _container.StartAsync();
            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(1433);
            _masterConn = $"Server={host},{port};Database=master;User Id=sa;Password={Password};TrustServerCertificate=True;Encrypt=False";
            await WaitForLoginAsync();

            // A fresh database name per run. A timed-out CREATE DATABASE can still land server-side, and a
            // reused name then collides on 1801 in a way that reads like a broken migration.
            var dbName = "cmms_load_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(_masterConn, $"CREATE DATABASE [{dbName}]");
            _dbConn = $"Server={host},{port};Database={dbName};User Id=sa;Password={Password};TrustServerCertificate=True;Encrypt=False";
        }
        catch (Exception ex)
        {
            Available = false;
            SkipReason = $"SQL Server container unavailable: {ex.Message}";
            return;
        }

        // Outside the try on purpose. Everything above is "can this machine run a container", which is a
        // legitimate skip. This is "does the pinned model's schema apply", which is a failure.
        await using (var db = NewContext())
            await db.Database.MigrateAsync();

        Available = true;
    }

    private async Task WaitForLoginAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await ExecuteAsync(_masterConn, "SELECT 1"); return; }
            catch (SqlException) when (attempt < 60) { await Task.Delay(2000); }
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A context over the migrated tenant database.
    ///
    /// No audit interceptor, unlike the API's fixture. The loader bulk-copies rows that ALREADY carry their
    /// audit rows from the artifact; an interceptor here would write a second set for writes the tests make
    /// while arranging, and the loader's preflight counts rows. A test that arranged its own fixture into a
    /// refusal would be a confusing way to discover that.
    ///
    /// CommandTimeout(60) gives a slow-but-working migration headroom; a healthy one takes about a second.
    /// </summary>
    public CmmsDbContext NewContext()
        => new(new DbContextOptionsBuilder<CmmsDbContext>()
                   .UseSqlServer(_dbConn, o => o.CommandTimeout(60))
                   .Options,
               ServiceLineContext.None);

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Every SQL suite here joins this ONE collection so xUnit runs them one at a time and never starts two SQL
/// Edge containers at once, which is the parallel-container crowding that flaked CI in cmms
/// (lnicoara/cmms#2148). A marker with no ICollectionFixture: each class keeps its own container and
/// database through IClassFixture, so there is no shared state to collide on, only shared scheduling.
/// </summary>
[CollectionDefinition("SqlSerial")]
public sealed class SqlSerialCollection { }
