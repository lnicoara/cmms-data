using System.Collections;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Emits rows for every tenant table the hand-written generator does not cover. lnicoara/cmms#2993.
///
/// Why this is model-driven rather than 143 hand-written emitters. #2876 already settled the argument for
/// the column manifest: "Derive the table manifest from CmmsDbContextModelSnapshot.cs rather than
/// hand-listing it, or it drifts on the next migration." The same reasoning governs rows. A hand-written
/// emitter per table is 143 places to forget, and every migration adds a 144th that nobody notices until an
/// operator opens an empty screen in pre-prod. Reading the model means a table that exists gets rows
/// because it exists, and a table added next month gets them without anyone remembering to.
///
/// What this deliberately does NOT try to be: faithful. The hand-written emitters own the tables whose
/// DISTRIBUTION is the thing under test, because #2880's ratios are what make a load test measure a
/// database anyone runs: WorkOrders, Assets, AuditEvents and the spine they hang on. This fills everything
/// else with rows that are structurally real (every FK resolves, every constraint holds, every non-nullable
/// column is populated) so the screens render, the joins execute, and the query planner sees a table with
/// cardinality instead of a table with nothing. That is a different job from modelling how a hospital
/// actually uses Purchase Orders, and conflating them is how the last attempt ended up covering 12 tables
/// well and 148 not at all.
///
/// Emission order is topological over foreign keys, so a child never references a parent that has not been
/// written. Rows the hand-written pass already produced are registered first, which is what lets a
/// synthesized ContractAsset point at a real Asset.
/// </summary>
public sealed class ModelSynthesizer
{
    private readonly ModelBridge _bridge;
    private readonly Deterministic _rng;
    private readonly GeneratorOptions _options;
    private readonly DateTimeOffset _cutoff;

    /// <summary>Primary keys already written, per table, so a foreign key can point at a row that exists.</summary>
    private readonly Dictionary<string, List<Guid>> _emitted = new(StringComparer.Ordinal);

    public ModelSynthesizer(
        ModelBridge bridge, Deterministic rng, GeneratorOptions options, DateTimeOffset cutoff)
    {
        _bridge = bridge;
        _rng = rng;
        _options = options;
        _cutoff = cutoff;
    }

    /// <summary>Tells the synthesizer about rows the hand-written pass wrote, so its FKs can reach them.</summary>
    public void Register(string table, IEnumerable<Guid> ids)
    {
        if (!_emitted.TryGetValue(table, out var list)) _emitted[table] = list = new List<Guid>();
        list.AddRange(ids);
    }

    /// <summary>
    /// Writes every table that is neither already covered nor exempt, in foreign-key order.
    /// Returns the tables it wrote, so the caller can record coverage.
    /// </summary>
    public IReadOnlyList<string> EmitAll(
        IReadOnlyCollection<string> alreadyCovered, Action<string, IReadOnlyDictionary<string, object?>> write)
    {
        var covered = new HashSet<string>(alreadyCovered, StringComparer.Ordinal);
        var todo = _bridge.AllTableNames()
            .Where(t => !covered.Contains(t) && !TableCoverage.Exempt.ContainsKey(t))
            .ToList();

        var written = new List<string>();
        foreach (var table in Order(todo))
        {
            var rows = RowsFor(table);
            if (rows == 0) continue;

            var et = _bridge.EntityTypeForTable(table)!;
            var columns = _bridge.ColumnsForTable(table);
            var ids = new List<Guid>();

            // Unique indexes, including FILTERED ones, are the constraint a generic pass cannot infer from
            // column metadata alone. IX_RiskAssessments_OneEnabledPerLine permits one enabled assessment
            // per service line, so three synthesized rows drawing from two service lines collide, and the
            // collision surfaces as a bulk-copy failure rather than as anything the generator can see about
            // itself. Rows that would duplicate a unique tuple are DROPPED rather than perturbed: changing
            // a value to dodge an index invents a row shaped by the index rather than by the domain, and a
            // table legitimately holding fewer rows than asked for is the honest outcome.
            // The PRIMARY KEY counts as a unique tuple too, and on the five tables that have no Id column
            // it is the only one. PmScheduleServiceLines is keyed (PmScheduleId, ServiceLineId), both of
            // them foreign keys drawn from small parent sets, so two rows collide readily. Deduping only
            // on unique INDEXES missed it, and a primary key violation is the one collision SqlBulkCopy
            // cannot be told to ignore.
            var uniqueTuples = et.GetIndexes().Where(ix => ix.IsUnique)
                                 .Select(ix => (IReadOnlyList<IProperty>)ix.Properties.ToList())
                                 .ToList();
            var pk = et.FindPrimaryKey();
            if (pk is not null) uniqueTuples.Add(pk.Properties.ToList());
            var seen = uniqueTuples.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToList();

            for (long i = 0; i < rows; i++)
            {
                var row = BuildRow(table, et, columns, i);
                Adjust(table, row, i);

                var collides = false;
                for (var u = 0; u < uniqueTuples.Count; u++)
                {
                    var key = string.Join('\u001f', uniqueTuples[u].Select(pr =>
                        row.TryGetValue(pr.Name, out var v) ? v?.ToString() ?? "\u0000" : "\u0000"));
                    if (!seen[u].Add(key)) collides = true;
                }
                if (collides) continue;

                write(table, row);
                if (row.TryGetValue("Id", out var idValue) && idValue is Guid g) ids.Add(g);
            }
            Register(table, ids);
            written.Add(table);
        }
        return written;
    }

    /// <summary>
    /// Parents before children, by Kahn over the model's foreign keys. A self-reference is a row-level
    /// ordering question inside one table, not a cycle between tables, so it is skipped here and handled by
    /// only ever pointing a row at an EARLIER row of the same table.
    /// </summary>
    private IReadOnlyList<string> Order(IReadOnlyList<string> tables)
    {
        var set = new HashSet<string>(tables, StringComparer.Ordinal);
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            var et = _bridge.EntityTypeForTable(t);
            var need = new HashSet<string>(StringComparer.Ordinal);
            if (et is not null)
                foreach (var fk in et.GetForeignKeys())
                {
                    var principal = fk.PrincipalEntityType.GetTableName();
                    if (principal is not null && principal != t && set.Contains(principal)) need.Add(principal);
                }
            deps[t] = need;
        }

        var ordered = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        // Bounded by construction: each pass either places a table or the graph has a cycle, and a cycle
        // among these tables would be a schema fact worth failing on rather than looping over.
        while (ordered.Count < tables.Count)
        {
            var ready = tables.Where(t => !done.Contains(t) && deps[t].All(done.Contains))
                              .OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                // Cycle. Place the rest alphabetically: their FKs are nullable often enough that a row can
                // still be written, and reporting the cycle beats refusing to emit anything at all.
                ready = tables.Where(t => !done.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            }
            foreach (var t in ready) { ordered.Add(t); done.Add(t); }
        }
        return ordered;
    }

    /// <summary>
    /// How many rows a synthesized table gets. Deliberately modest and uniform: these tables exist here so
    /// their screens render and their joins execute, and #2880's volume argument is about the tables the
    /// hand-written pass owns. Scaling every one of 143 tables would inflate the artifact without making
    /// the load test measure anything it does not already measure.
    /// </summary>
    private int RowsFor(string table) =>
        // Organizations is a singleton: a unique index over the SingletonGuard shadow column plus
        // CK_Organization_Singleton mean a second row cannot be inserted. It is generated rather than
        // exempt because --clear-target empties it (Campuses.OrganizationId is a non-nullable reference
        // into it), so the artifact has to put back what the clear takes away or the location tree
        // cannot load at all.
        table == "Organizations" ? 1 : Math.Max(1, _options.SynthesizedRowsPerTable);

    private Dictionary<string, object?> BuildRow(
        string table, IEntityType et, IReadOnlyList<IProperty> columns, long ordinal)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fkTargets = ForeignKeyTargets(et);

        foreach (var p in columns)
        {
            if (fkTargets.TryGetValue(p.Name, out var principal))
            {
                row[p.Name] = ForeignKeyValue(table, principal, p, ordinal);
                continue;
            }
            row[p.Name] = ScalarValue(table, p, ordinal);
        }
        return row;
    }

    /// <summary>Column name to the table it references. Composite keys map each column to the same parent.</summary>
    private static Dictionary<string, string> ForeignKeyTargets(IEntityType et)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fk in et.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType.GetTableName();
            if (principal is null) continue;
            foreach (var p in fk.Properties) map[p.Name] = principal;
        }
        return map;
    }

    private object? ForeignKeyValue(string table, string principal, IProperty p, long ordinal)
    {
        // A self-reference points at an earlier row of this same table, or nothing for the first one, so a
        // parent always exists by the time a child names it.
        if (principal == table)
        {
            if (ordinal == 0) return p.IsNullable ? null : (object?)Guid.Empty;
            return _emitted.TryGetValue(table, out var own) && own.Count > 0
                ? own[(int)(ordinal % own.Count)] : null;
        }

        if (_emitted.TryGetValue(principal, out var ids) && ids.Count > 0)
            return ids[(int)(_rng.Next(table, ordinal, p.Name) % (ulong)ids.Count)];

        // No parent row exists. Nullable means the honest answer is null; non-nullable means the column
        // would dangle, and a dangling FK is exactly what the artifact's closed-reference-graph contract
        // forbids, so the row is not written rather than written broken.
        return p.IsNullable ? null : throw new InvalidOperationException(
            $"{table}.{p.Name} requires a row in '{principal}', which has none. Emission order is wrong.");
    }

    private object? ScalarValue(string table, IProperty p, long ordinal)
    {
        var t = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        var name = p.Name;

        if (p.IsPrimaryKey() && t == typeof(Guid)) return _rng.Id(table, ordinal, name);

        // Soft-delete and provenance, which the interceptor owns at runtime and no bulk insert supplies.
        if (name == "IsDeleted") return false;
        if (name is "DeletedAtUtc") return null;
        if (name is "DeletedBy") return null;
        if (name is "CreatedAtUtc" or "ModifiedAtUtc") return _cutoff.AddDays(-(ordinal % 365) - 1);
        if (name is "CreatedBy" or "ModifiedBy") return "load-test";

        // ISJSON check constraints cover 20 columns and every one of them is named *Json. A non-JSON
        // string in any of them fails the constraint at load, not at generation, which is the expensive
        // place to find out.
        if (t == typeof(string) && name.EndsWith("Json", StringComparison.Ordinal))
            return name.Contains("Ids", StringComparison.Ordinal) ? "[]" : "{}";

        if (t == typeof(string))
        {
            var max = p.GetMaxLength() ?? 64;
            // Unique-indexed columns carry the ordinal so a table with a UNIQUE index can hold more than
            // one synthesized row.
            var text = $"LT-{table}-{ordinal}";
            return text.Length <= max ? text : text[..max];
        }

        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            return values.GetValue((int)(_rng.Next(table, ordinal, name) % (ulong)values.Length));
        }

        if (t == typeof(bool)) return _rng.Chance(table, ordinal, name, 0.5);
        if (t == typeof(int)) return (int)(_rng.Next(table, ordinal, name) % 1000);
        if (t == typeof(long)) return (long)(_rng.Next(table, ordinal, name) % 1000);
        if (t == typeof(short)) return (short)(_rng.Next(table, ordinal, name) % 100);
        if (t == typeof(byte)) return (byte)(_rng.Next(table, ordinal, name) % 100);
        if (t == typeof(decimal)) return Math.Round((decimal)(_rng.NextDouble(table, ordinal, name) * 1000), 2);
        if (t == typeof(double)) return Math.Round(_rng.NextDouble(table, ordinal, name) * 1000, 2);
        if (t == typeof(float)) return (float)Math.Round(_rng.NextDouble(table, ordinal, name) * 1000, 2);
        if (t == typeof(Guid)) return _rng.Id(table, ordinal, name);
        if (t == typeof(DateOnly)) return DateOnly.FromDateTime(_cutoff.AddDays(-(ordinal % 365) - 1).UtcDateTime);
        if (t == typeof(DateTimeOffset)) return _cutoff.AddDays(-(ordinal % 365) - 1);
        if (t == typeof(DateTime)) return _cutoff.AddDays(-(ordinal % 365) - 1).UtcDateTime;
        if (t == typeof(TimeSpan)) return TimeSpan.FromMinutes(ordinal % 480);
        if (t == typeof(TimeOnly)) return new TimeOnly(8, 0);
        if (t == typeof(byte[])) return p.IsNullable ? null : Array.Empty<byte>();

        // Unknown CLR type. Null when the column allows it; otherwise fail loudly, because a silently
        // skipped column becomes a NOT NULL violation at load time on somebody else's machine.
        return p.IsNullable
            ? null
            : throw new InvalidOperationException($"{table}.{name}: no synthesized value for {t.Name}.");
    }

    /// <summary>
    /// The handful of table-specific rules a generic pass cannot read off the model, each one a check
    /// constraint or a query filter that would otherwise reject or hide the row.
    /// </summary>
    private void Adjust(string table, Dictionary<string, object?> row, long ordinal)
    {
        // CK_Organization_Singleton pins the guard column to 1; a synthesized byte would fail the check.
        if (table == "Organizations" && row.ContainsKey("SingletonGuard")) row["SingletonGuard"] = (byte)1;

        // CK_WorkOrderPhoto_StepAsset: StepId and AssetId are both set or both null.
        if (table == "WorkOrderPhotos" && row.ContainsKey("StepId") && row.ContainsKey("AssetId"))
        {
            if (row["StepId"] is null || row["AssetId"] is null) { row["StepId"] = null; row["AssetId"] = null; }
        }

        // A Guid.Empty ServiceLineId is filtered out of every application query by the global filter, so
        // the row would load and then be invisible, which reads as an empty screen rather than as a bug.
        if (row.TryGetValue("ServiceLineId", out var sl) && sl is Guid g && g == Guid.Empty)
            row["ServiceLineId"] = ServiceLineFallback();

        // Provenance ordering is an invariant the audit reader relies on.
        if (row.TryGetValue("CreatedAtUtc", out var c) && row.TryGetValue("ModifiedAtUtc", out var m)
            && c is DateTimeOffset cd && m is DateTimeOffset md && md < cd)
            row["ModifiedAtUtc"] = cd;
    }

    private Guid ServiceLineFallback() =>
        _emitted.TryGetValue("ServiceLines", out var ids) && ids.Count > 0
            ? ids[0]
            : Cmms.Domain.Organization.ServiceLineSeed.ClinicalEngineeringId;
}
