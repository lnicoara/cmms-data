using Cmms.Domain.Auditing;
using Cmms.Infrastructure.Auditing;
using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// The bridge to the REAL EF model, and the reason this generator can claim its rows are faithful.
///
/// It builds a <see cref="CmmsDbContext"/> over the SQL Server provider with a connection string that is
/// never opened. EF resolves the full model (column names, types, value generation, keys) without any
/// database, so the column manifest is derived rather than hand-listed. A hand-listed manifest rots on
/// the next migration, silently, and the failure surfaces as a corrupt bulk load much later.
///
/// The same trick produces a real <c>EntityEntry</c>, which is what lets audit rows go through the
/// product's own <see cref="AuditDiff"/> and <see cref="AuditEventFactory"/> (extracted for exactly this
/// in lnicoara/cmms#2903) instead of a second implementation that would drift.
/// </summary>
public sealed class ModelBridge : IDisposable
{
    private readonly CmmsDbContext _db;
    private readonly Dictionary<Type, IReadOnlyList<IProperty>> _columns = new();

    public ModelBridge()
    {
        // Never opened. Present only so the SQL Server provider can build its model, which is the model
        // the artifact must match.
        var options = new DbContextOptionsBuilder<CmmsDbContext>()
            .UseSqlServer("Server=offline;Database=offline;Trusted_Connection=False;")
            .Options;
        _db = new CmmsDbContext(options, ServiceLineContext.None);
    }

    public string TableName<T>() where T : class
        => _db.Model.FindEntityType(typeof(T))?.GetTableName()
           ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    /// <summary>
    /// The columns the artifact must carry for <typeparamref name="T"/>: every mapped scalar EXCEPT the
    /// ones SQL Server generates itself and rejects an explicit insert into.
    ///
    /// There are exactly two of those, and the distinction is easy to get wrong in the expensive direction.
    /// <c>WorkOrder.RowVersion</c> is a <c>rowversion</c> column; <c>PredictiveEvent.Seq</c> is IDENTITY.
    /// Both are ordinary public CLR properties, so reflecting over the class instead of asking the model
    /// would include them and the bulk insert would fail on its first batch.
    ///
    /// What must NOT be excluded is a client-assigned key. EF's convention marks a Guid primary key
    /// <see cref="ValueGenerated.OnAdd"/> even though <c>Entity.Id</c> is assigned in C# before EF ever
    /// sees the row, so filtering on "not ValueGenerated.Never" silently drops the primary key from every
    /// table. That produced an artifact whose rows had no <c>Id</c> at all and whose every foreign key
    /// dangled. The self-validation pass caught it; nothing else would have until load time.
    /// </summary>
    public IReadOnlyList<IProperty> Columns<T>() where T : class
    {
        if (_columns.TryGetValue(typeof(T), out var cached)) return cached;

        var et = _db.Model.FindEntityType(typeof(T))
                 ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

        var cols = et.GetProperties().Where(p => !IsStoreGenerated(p)).ToList();

        // A row without its key is unloadable and every reference to it dangles, so fail loudly here
        // rather than emit millions of rows that only fall over much later.
        var key = et.FindPrimaryKey();
        if (key is not null)
        {
            var missing = key.Properties.Where(k => !cols.Contains(k)).Select(k => k.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}: primary key column(s) {string.Join(", ", missing)} excluded from the manifest.");
        }

        _columns[typeof(T)] = cols;
        return cols;
    }

    /// <summary>
    /// The mapped entity type behind a table name, or null when the artifact names a table this model
    /// does not have. Table-oriented rather than generic because the LOADER (lnicoara/cmms#2908) walks the
    /// artifact's table list at runtime and has no compile-time type to work from.
    /// </summary>
    public IEntityType? EntityTypeForTable(string table) =>
        _db.Model.GetEntityTypes().FirstOrDefault(
            et => string.Equals(et.GetTableName(), table, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every mapped table in the tenant model, not just the ones the artifact carries.
    ///
    /// The loader needs this to answer a question the artifact cannot: which OTHER tables point at the
    /// artifact's tables. Emptying a table the rest of the schema still references fails on the foreign key,
    /// and the artifact's own table list is blind to everything outside it.
    /// </summary>
    public IReadOnlyList<string> AllTableNames() =>
        _db.Model.GetEntityTypes()
            .Select(et => et.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <see cref="Columns{T}"/> addressed by table name, so the loader writes against exactly the manifest
    /// the generator wrote with. Two derivations of "which columns belong in the artifact" would drift on
    /// the next migration, and the drift would surface as a corrupt bulk load rather than a build error.
    /// </summary>
    public IReadOnlyList<IProperty> ColumnsForTable(string table)
    {
        var et = EntityTypeForTable(table)
                 ?? throw new InvalidOperationException($"No mapped entity for table '{table}'.");

        var cols = et.GetProperties().Where(p => !IsStoreGenerated(p)).ToList();

        var key = et.FindPrimaryKey();
        if (key is not null)
        {
            var missing = key.Properties.Where(k => !cols.Contains(k)).Select(k => k.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"{table}: primary key column(s) {string.Join(", ", missing)} excluded from the manifest.");
        }
        return cols;
    }

    private static bool IsStoreGenerated(IProperty p)
    {
        // IDENTITY: the server assigns it and refuses an explicit value.
        if (p.GetValueGenerationStrategy() == SqlServerValueGenerationStrategy.IdentityColumn) return true;

        // rowversion / timestamp: server-maintained, insert is rejected outright.
        var store = p.GetColumnType();
        if (store is not null &&
            (store.Equals("rowversion", StringComparison.OrdinalIgnoreCase) ||
             store.Equals("timestamp", StringComparison.OrdinalIgnoreCase)))
            return true;

        return p.IsConcurrencyToken && p.ValueGenerated == ValueGenerated.OnAddOrUpdate
               && p.ClrType == typeof(byte[]);
    }

    /// <summary>
    /// Every mapped property name, INCLUDING the store-generated ones. Wider than <see cref="Columns{T}"/>
    /// on purpose: the audit diff iterates the full property set, so a create diff legitimately names
    /// <c>RowVersion</c> even though no artifact row may carry that column.
    /// </summary>
    public HashSet<string> AllPropertyNames<T>() where T : class
        => (_db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped."))
            .GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>One artifact row: column name to CLR value, straight off the entity.</summary>
    public Dictionary<string, object?> Row<T>(T entity) where T : class
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        var entry = _db.Entry(entity);
        foreach (var p in Columns<T>())
            row[p.GetColumnName()] = entry.Property(p.Name).CurrentValue;
        Detach(entity);
        return row;
    }

    /// <summary>
    /// The audit row the interceptor would have written for creating this entity, produced by the
    /// product's own code rather than a copy of it.
    /// </summary>
    public AuditEvent CreatedAudit<T>(
        T entity, DateTimeOffset now, string? actorId, string actorName, string? correlationId)
        where T : class
    {
        var entry = _db.Entry(entity);
        entry.State = EntityState.Added;
        var ev = AuditEventFactory.Create(entry, AuditAction.Created, now, actorId, actorName, correlationId);
        Detach(entity);
        return ev;
    }

    /// <summary>
    /// The audit row for an update touching <paramref name="modifiedProperties"/>.
    ///
    /// The interceptor's diff keys off <c>prop.IsModified</c> and never off value inequality, so an update
    /// diff is "the fields the endpoint wrote", not "the fields the user changed". Marking properties
    /// explicitly here reproduces that, including the wide diffs a form post produces.
    /// </summary>
    public AuditEvent UpdatedAudit<T>(
        T entity, IReadOnlyList<string> modifiedProperties,
        DateTimeOffset now, string? actorId, string actorName, string? correlationId,
        Action<T>? mutate = null)
        where T : class
    {
        var entry = _db.Entry(entity);

        // Unchanged first, which snapshots the current values as ORIGINAL. Only then mutate, so the
        // resulting diff carries a genuine old/new pair rather than the same value twice.
        entry.State = EntityState.Unchanged;
        mutate?.Invoke(entity);
        entry.DetectChanges();

        // Marked explicitly on top of whatever the mutation touched. The interceptor's diff keys off
        // IsModified and never off value inequality, so a form post that rebinds twenty fields writes
        // twenty keys even where nothing changed. Reproducing that is the difference between a realistic
        // update-row size and one a third of it.
        foreach (var name in modifiedProperties)
            entry.Property(name).IsModified = true;

        var ev = AuditEventFactory.Create(entry, AuditAction.Updated, now, actorId, actorName, correlationId);
        Detach(entity);
        return ev;
    }

    /// <summary>
    /// The audit row for a soft delete. The interceptor rewrites a hard delete into an update and records
    /// the transition, so this diff is always exactly the IsDeleted flip and never the whole entity.
    /// </summary>
    public AuditEvent DeletedAudit<T>(
        T entity, DateTimeOffset now, string? actorId, string actorName, string? correlationId)
        where T : class
    {
        var entry = _db.Entry(entity);
        entry.State = EntityState.Unchanged;
        var ev = AuditEventFactory.Create(entry, AuditAction.Deleted, now, actorId, actorName, correlationId);
        Detach(entity);
        return ev;
    }

    private long _ops;

    // The change tracker must not grow across millions of rows. Detaching after each use releases the
    // entry, but the tracker's internal structures still accumulate over millions of attach/detach
    // cycles, so a periodic full clear is what actually keeps the working set flat across a
    // 4,000,000-row run rather than merely slowing the growth.
    private void Detach<T>(T entity) where T : class
    {
        _db.Entry(entity).State = EntityState.Detached;
        if (++_ops % 50_000 == 0) _db.ChangeTracker.Clear();
    }

    public void Dispose() => _db.Dispose();
}
