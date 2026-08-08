using Cmms.LoadDataGenerator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmms.LoadDataRunner;

/// <summary>One table's place in the load: its chunks, its column binders, and its primary key.</summary>
public sealed record TablePlan(
    string Table,
    IReadOnlyList<ChunkRef> Chunks,
    IReadOnlyList<ColumnBinder> Binders,
    IReadOnlyList<string> KeyColumns)
{
    public long Rows => Chunks.Sum(c => c.Rows);
}

/// <summary>
/// What the loader intends to do, computed before it is allowed to do any of it.
///
/// Load order is DERIVED from the model's foreign keys rather than hardcoded. A hardcoded list is right
/// until someone adds a relationship, and then it is wrong in a way nothing catches: SqlBulkCopy does not
/// check constraints by default, so loading a child before its parent does not error, it quietly writes
/// rows whose references dangle.
/// </summary>
public sealed class LoadPlan
{
    public IReadOnlyList<TablePlan> Tables { get; }
    public long TotalRows => Tables.Sum(t => t.Rows);
    public int TotalChunks => Tables.Sum(t => t.Chunks.Count);

    private LoadPlan(IReadOnlyList<TablePlan> tables) => Tables = tables;

    public static LoadPlan Build(Artifact artifact, ModelBridge model)
    {
        var order = DependencyOrder(artifact.Tables, model);

        var plans = new List<TablePlan>(order.Count);
        foreach (var table in order)
        {
            var properties = model.ColumnsForTable(table);
            var binders = properties.Select(ColumnBinder.For).ToList();
            var key = model.EntityTypeForTable(table)!.FindPrimaryKey();
            var keyColumns = key?.Properties.Select(p => p.GetColumnName()!).ToList() ?? new List<string>();
            var chunks = artifact.Chunks[table];
            // The binders the artifact can actually feed, which is not always all of them: a column the
            // model gained since generation has no value to bind, so it is dropped here rather than
            // carried into the reader and the column mappings.
            var loadable = VerifyColumns(table, chunks[0].Path, binders);
            plans.Add(new TablePlan(table, chunks, loadable, keyColumns));
        }
        return new LoadPlan(plans);
    }

    /// <summary>
    /// Checks the artifact's columns against the model's for one table, and returns the binders that can
    /// actually be fed, which is the subset the rest of the load must use.
    ///
    /// This is the guard against loading an artifact across a migration boundary. A column the model gained
    /// since generation has no value on any row; a column the model lost would fail somewhere deep in a bulk
    /// copy with a message about the destination rather than the cause. One line of one chunk per table
    /// settles it before anything is written.
    ///
    /// RETURNING the surviving binders is the load-bearing part, not a convenience. Merely tolerating a
    /// missing nullable column at plan time is a bug that looks like a fix: ChunkDataReader treats an absent
    /// column as corruption and throws on its first row, and SqlBulkCopy would still carry a column mapping
    /// for a column the reader never yields. Both consume this list, so dropping the binder here is what
    /// makes "the bulk copy simply does not supply it" true rather than merely intended.
    /// </summary>
    private static IReadOnlyList<ColumnBinder> VerifyColumns(
        string table, string chunkPath, IReadOnlyList<ColumnBinder> binders)
    {
        using var file = File.OpenRead(chunkPath);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var line = reader.ReadLine();
        if (line is null) throw new InvalidOperationException($"Chunk '{chunkPath}' is empty.");

        using var doc = System.Text.Json.JsonDocument.Parse(line);
        var inArtifact = doc.RootElement.EnumerateObject()
            .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inModel = binders.Select(b => b.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = binders.Where(b => !inArtifact.Contains(b.ColumnName)).ToList();
        var extra = inArtifact.Except(inModel, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

        // A missing NULLABLE column is tolerated, and that is not laxness: this repo's migrations are
        // additive by convention, so an artifact generated on Monday legitimately lacks Tuesday's new
        // nullable column. SqlBulkCopy simply does not supply it and the row gets NULL, which is precisely
        // what a row written before that migration would hold. Refusing outright meant regenerating the
        // whole 3.7 GB artifact, an hour's work, every time any nullable column landed on main, which on
        // an active day is a race the artifact cannot win.
        var missingRequired = missing.Where(b => b.IsRequired)
            .Select(b => b.ColumnName).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var missingOptional = missing.Where(b => !b.IsRequired)
            .Select(b => b.ColumnName).OrderBy(c => c, StringComparer.Ordinal).ToList();

        if (missingRequired.Count > 0 || extra.Count > 0)
        {
            var parts = new List<string>();
            if (missingRequired.Count > 0)
                parts.Add($"the model REQUIRES {string.Join(", ", missingRequired)}, which the artifact lacks and cannot be left NULL");
            if (extra.Count > 0)
                parts.Add($"the artifact carries {string.Join(", ", extra)} which the model has no column for");
            throw new InvalidOperationException(
                $"Table '{table}' does not match the model: {string.Join("; ", parts)}. " +
                "The artifact was generated against a different schema; regenerate it rather than loading it.");
        }

        if (missingOptional.Count == 0) return binders;

        // Said plainly because it is a real difference from production, not a formality. If the migration
        // that added the column also backfilled a value for existing rows, the loaded rows will still be
        // NULL. For load-test volume that is fine; for a column under test it is not, and the operator is
        // the one who can tell those apart.
        Console.WriteLine(
            $"  note: {table} will load without {string.Join(", ", missingOptional)}; " +
            "the artifact predates those nullable columns, so those rows will be NULL even if the " +
            "migration backfilled a value.");

        return binders.Where(b => inArtifact.Contains(b.ColumnName)).ToList();
    }

    /// <summary>
    /// Every table that has to be emptied for the artifact's tables to be emptiable, in dependency order.
    ///
    /// The artifact's own table list is the WRONG answer to "what does clearing touch", and a real run
    /// proved it. Clearing walked the artifact's 12 tables in reverse order and died on the ninth:
    ///   The DELETE statement conflicted with the REFERENCE constraint "FK_Items_ServiceLines_ServiceLineId"
    /// Items is not in the artifact, so nothing in the plan knew it existed, but it points at ServiceLines
    /// and DELETE always checks foreign keys. Eight tables had already been emptied and committed by then.
    ///
    /// So the set is the transitive closure of "references something already in the set", seeded with the
    /// artifact's tables and grown to a fixpoint. That is wider than the artifact and deliberately so: a
    /// tenant that still holds rows pointing at a ServiceLine the artifact is about to re-create on the same
    /// Guid is not a loadable target. It is NOT the whole database, which would take out Organization,
    /// ConfigSettings, and FieldDefinitions, none of which reference the artifact and none of which the
    /// artifact could put back.
    /// </summary>
    public static IReadOnlyList<string> ClearOrder(IReadOnlyList<string> artifactTables, ModelBridge model)
    {
        var closure = new HashSet<string>(artifactTables, StringComparer.OrdinalIgnoreCase);
        var all = model.AllTableNames();

        bool grew;
        do
        {
            grew = false;
            foreach (var table in all)
            {
                if (closure.Contains(table)) continue;
                var et = model.EntityTypeForTable(table);
                if (et is null) continue;

                foreach (var fk in et.GetForeignKeys())
                {
                    var principal = fk.PrincipalEntityType.GetTableName();
                    if (principal is null) continue;
                    // A self-reference is a row-level ordering question inside one table, not a reason to
                    // pull the table into the closure.
                    if (string.Equals(principal, table, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!closure.Contains(principal)) continue;
                    closure.Add(table);
                    grew = true;
                    break;
                }
            }
        } while (grew);

        // Optional edges ARE relaxed here, and that is only sound because the caller nulls every nullable
        // foreign key in this closure before it deletes anything (TargetCleaner.ClearAsync). Once those
        // columns hold NULL the only edges a DELETE can trip over are the required ones, and required
        // edges are acyclic by the check above. Deleting without that nulling pass fails on
        // FK_PoApprovalRules_AccessGroups_ApproverAccessGroupId and its peers. lnicoara/cmms#2993.
        return DependencyOrder(closure.ToList(), model, relaxOptional: true);
    }

    /// <summary>
    /// Parents before children, by Kahn's algorithm over the model's foreign keys, restricted to the tables
    /// the artifact actually carries. Self-references are ignored: a work order pointing at a parent work
    /// order is a row-level ordering question inside one table, not a table-level one.
    ///
    /// The graph is NOT a DAG, and pretending otherwise would have been a bug. ServiceLine.SupervisorId
    /// references Labor (so an automation can dot-walk to the supervisor's email, lnicoara/cmms#1581) while
    /// Labor.ServiceLineId references ServiceLines, which is a genuine two-table cycle in the real model.
    ///
    /// Both of those edges are OPTIONAL, and that is what makes the cycle loadable: a nullable foreign key
    /// need not be satisfied at insert time. So optional edges are preferred but relaxed when they are the
    /// only thing blocking progress, while required edges are never relaxed. A cycle made of required
    /// foreign keys genuinely has no valid load order, and that stops the run.
    /// </summary>
    /// <param name="relaxOptional">
    /// TRUE for LOAD order, where a nullable foreign key need not be satisfied at insert time, so an
    /// optional edge may be relaxed to break a cycle. FALSE for DELETE order, where it may not: DELETE
    /// checks every foreign key regardless of nullability, so relaxing an optional edge produces an order
    /// that deletes a parent while a child still points at it. lnicoara/cmms#2993.
    ///
    /// At 12 covered tables the relaxation almost never fired and the two orders were interchangeable in
    /// practice. Covering all 158 made optional cycles ordinary, and the reverse walk started failing on
    /// FK_PoApprovalRules_AccessGroups_ApproverAccessGroupId and its peers.
    /// </param>
    private static List<string> DependencyOrder(
        IReadOnlyList<string> tables, ModelBridge model, bool relaxOptional = true)
    {
        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        var required = tables.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var optional = tables.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            var et = model.EntityTypeForTable(table)
                     ?? throw new InvalidOperationException(
                         $"Artifact carries table '{table}' but the model has no entity mapped to it.");

            foreach (var fk in et.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType.GetTableName();
                if (principal is null) continue;
                if (string.Equals(principal, table, StringComparison.OrdinalIgnoreCase)) continue;
                if (!set.Contains(principal)) continue;
                (fk.IsRequired ? required[table] : optional[table]).Add(principal);
            }
        }

        // Deterministic: ready tables come out in name order, so the same artifact always produces the same
        // plan and a printed plan can be diffed between runs.
        var ordered = new List<string>(tables.Count);
        var remaining = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            var ready = Ready(t => required[t].Concat(optional[t]));
            if (ready.Count == 0 && relaxOptional) ready = Ready(t => required[t]);

            if (ready.Count == 0)
                throw new InvalidOperationException(
                    (relaxOptional
                        ? "Required foreign keys among the artifact's tables form a cycle, so no load order "
                        : "Foreign keys among the artifact's tables form a cycle that no DELETE order can "
                          + "satisfy, because DELETE checks optional keys too. Break it by nulling one of "
                          + "them before the delete. Cycle: ") +
                    (relaxOptional ? "satisfies them: " : "") +
                    string.Join(", ", remaining.OrderBy(t => t, StringComparer.Ordinal)));

            foreach (var t in ready) { ordered.Add(t); remaining.Remove(t); }

            List<string> Ready(Func<string, IEnumerable<string>> deps) => remaining
                .Where(t => deps(t).All(d => !remaining.Contains(d)))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
        }
        return ordered;
    }

    /// <summary>
    /// The nullable foreign-key columns on a table, which the clear blanks before deleting anything.
    ///
    /// A nullable foreign key needs no parent at INSERT, which is why load order may relax it. DELETE has
    /// no such allowance: it checks the constraint whatever the column's nullability, so an optional cycle
    /// that loads cleanly has no valid delete order at all until those columns are emptied.
    /// </summary>
    public static IReadOnlyList<string> NullableForeignKeyColumns(string table, ModelBridge model)
    {
        var et = model.EntityTypeForTable(table);
        if (et is null) return Array.Empty<string>();
        return et.GetForeignKeys()
            .Where(fk => !fk.IsRequired)
            .SelectMany(fk => fk.Properties)
            .Where(p => p.IsNullable && !p.IsPrimaryKey())
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    public IEnumerable<string> Describe()
    {
        yield return $"{Tables.Count} tables, {TotalChunks:N0} chunks, {TotalRows:N0} rows, in load order:";
        foreach (var t in Tables)
            yield return $"  {t.Table,-18} {t.Rows,12:N0} rows  {t.Chunks.Count,4} chunks  {t.Binders.Count,3} columns";
    }
}
