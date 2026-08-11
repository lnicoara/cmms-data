using System.IO.Compression;
using System.Text.Json;
using Cmms.LoadDataGenerator;
using Microsoft.EntityFrameworkCore;

namespace Cmms.LoadDataRunner.Tests;

// lnicoara/cmms#2908: the load plan (order and column agreement) and the streaming reader (type
// conversion). No database: the model is resolved offline the same way the generator resolves it.
public class PlanAndReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loader-plan-" + Guid.NewGuid().ToString("N"));
    private readonly ModelBridge _model = new();

    private LoadPlan BuildPlan(string name, Action<string>? corrupt = null)
    {
        var dir = ArtifactTests.Build(Path.Combine(_dir, name));
        corrupt?.Invoke(dir);
        return LoadPlan.Build(Artifact.Open(dir), _model);
    }

    // The clear set is NOT the artifact's table list, and a real pre-prod run is what proved it. Clearing
    // walked the artifact's 12 tables in reverse and died on the ninth:
    //   The DELETE statement conflicted with the REFERENCE constraint "FK_Items_ServiceLines_ServiceLineId"
    // Items is not in the artifact, so the plan had never heard of it, but it points at ServiceLines and
    // DELETE always checks foreign keys. Eight tables were already emptied and committed by then.
    [Fact]
    public void Clear_set_includes_tables_outside_the_artifact_that_reference_it()
    {
        var plan = BuildPlan("clearset");
        var artifactTables = plan.Tables.Select(t => t.Table).ToList();

        var order = LoadPlan.ClearOrder(artifactTables, _model).ToList();

        // The exact table that broke the real run.
        Assert.Contains("Items", order);
        // And it must be deleted BEFORE the table it references, or the foreign key fails again.
        Assert.True(order.IndexOf("ServiceLines") < order.IndexOf("Items"),
            "ServiceLines must precede Items in dependency order, so the reverse walk deletes Items first");

        // Everything the artifact owns still gets cleared.
        foreach (var t in artifactTables) Assert.Contains(t, order);
    }

    // Widening the clear must not become "empty the database". The rule that governs it has CHANGED, and
    // this test now pins the new one, so the reversal is visible here rather than silent.
    //
    // It used to name five tables the clear had to leave alone: Organizations, ConfigSettings,
    // FieldDefinitions, Campuses, Departments. Its stated reason was that "the artifact could not put them
    // back", which was true when the generator emitted 12 of 162 tables. lnicoara/cmms#2993 made the
    // artifact cover every table, so the condition the rule rested on no longer holds, and keeping the
    // old list would now FORBID covering the location tree: Campus.OrganizationId is a non-nullable
    // reference into Organizations, and Organization.Id is a fresh Guid minted per tenant at provisioning,
    // so an offline artifact cannot point at the existing row and must supply one.
    //
    // The invariant underneath survives intact and is what is asserted now: the clear may only remove what
    // the artifact can restore. That was always the real rule; the list of five was one era's shorthand for
    // it, and a shorthand stops being safe the moment the thing it stood for moves.
    [Fact]
    public void Clear_only_removes_what_the_artifact_can_put_back()
    {
        var plan = BuildPlan("clearscope");
        var artifactTables = plan.Tables.Select(t => t.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = LoadPlan.ClearOrder(artifactTables.ToList(), _model);

        // Every table the clear empties is either one the artifact writes, or one it empties solely because
        // that table REFERENCES something the artifact owns and the delete would otherwise fail a foreign
        // key. Nothing else may be in the set.
        var unrestorable = order
            .Where(t => !artifactTables.Contains(t))
            .Where(t => _model.EntityTypeForTable(t)?.GetForeignKeys()
                .All(fk => !artifactTables.Contains(fk.PrincipalEntityType.GetTableName() ?? "")) ?? true)
            .ToList();

        Assert.True(!unrestorable.Any(),
            "the clear would empty tables the artifact neither writes nor has to clear for a foreign key, " +
            "leaving the tenant unusable rather than merely empty: " + string.Join(", ", unrestorable));
    }

    // SqlBulkCopy does not check constraints, so loading a child before its parent does NOT error. It
    // silently writes rows whose references dangle, and the artifact looks fine until something reads it.
    [Fact]
    public void Orders_parents_before_the_tables_that_reference_them()
    {
        var order = BuildPlan("order").Tables.Select(t => t.Table).ToList();

        int At(string t) => order.IndexOf(t);
        Assert.True(At("ServiceLines") < At("Assets"), "ServiceLines must precede Assets");
        Assert.True(At("AssetTypes") < At("Assets"), "AssetTypes must precede Assets");
        Assert.True(At("Assets") < At("WorkOrders"), "Assets must precede WorkOrders");
        Assert.True(At("WorkOrders") < At("WorkOrderLabor"), "WorkOrders must precede WorkOrderLabor");
        Assert.True(At("Labor") < At("WorkOrderLabor"), "Labor must precede WorkOrderLabor");
    }

    [Fact]
    public void Order_is_deterministic_across_builds()
    {
        var first = BuildPlan("det1").Tables.Select(t => t.Table);
        var second = BuildPlan("det2").Tables.Select(t => t.Table);
        Assert.Equal(first, second);
    }

    // AuditEvents is 344 of the full artifact's 584 chunks and 34.4M of its 42.7M rows, and it has no
    // foreign keys, so Kahn puts it in the first dependency round where the alphabetical tie-break lands it
    // near the front. At the rate measured on 2026-08-10 that is roughly 63 hours before the second table
    // starts, with everything an operator would query queued behind the least interesting 80% of the rows.
    [Fact]
    public void Loads_AuditEvents_last()
    {
        var order = BuildPlan("deferred").Tables.Select(t => t.Table).ToList();

        Assert.Contains("AuditEvents", order);
        Assert.Equal("AuditEvents", order[^1]);
    }

    // The property the deferral rests on, asserted against the MODEL rather than against the plan, so this
    // fails the day someone gives AuditEvent a relationship even if nothing else notices. An audit row names
    // its subject by the EntityType and EntityId strings on purpose: it has to outlive the row it describes,
    // which is what an append-only Part 11 trail is for.
    [Fact]
    public void AuditEvents_has_no_foreign_key_in_either_direction()
    {
        var et = _model.EntityTypeForTable("AuditEvents");
        Assert.NotNull(et);

        var outbound = et!.GetForeignKeys()
            .Select(fk => fk.PrincipalEntityType.GetTableName())
            .Where(p => p is not null && !string.Equals(p, "AuditEvents", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(!outbound.Any(),
            "AuditEvents now references " + string.Join(", ", outbound) +
            ", so its load position is a real constraint and it may no longer be deferred");

        var inbound = _model.AllTableNames()
            .Where(t => !string.Equals(t, "AuditEvents", StringComparison.OrdinalIgnoreCase))
            .Where(t => _model.EntityTypeForTable(t)?.GetForeignKeys()
                .Any(fk => string.Equals(fk.PrincipalEntityType.GetTableName(), "AuditEvents",
                    StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
        Assert.True(!inbound.Any(),
            string.Join(", ", inbound) + " now reference AuditEvents, so it may no longer be deferred");
    }

    // Moving a table to the end must MOVE it, not drop it or leave a copy behind. A plan that quietly lost
    // AuditEvents would load without error and simply never write 34.4M rows.
    [Fact]
    public void Deferring_a_table_neither_drops_nor_duplicates_any_table()
    {
        var artifact = Artifact.Open(ArtifactTests.Build(Path.Combine(_dir, "nodrop")));
        var planned = LoadPlan.Build(artifact, _model).Tables.Select(t => t.Table).ToList();

        Assert.Equal(artifact.Tables.Count, planned.Count);
        Assert.Equal(artifact.Tables.Count, planned.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(artifact.Tables.Except(planned, StringComparer.Ordinal));
    }

    // The deferral must not disturb the order it is layered on top of. Every dependency edge the sort
    // established still has to hold after the move, which is the whole reason only an isolated node may be
    // listed.
    [Fact]
    public void Deferring_preserves_every_dependency_edge()
    {
        var order = BuildPlan("edges").Tables.Select(t => t.Table).ToList();
        var position = order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        foreach (var table in order)
        foreach (var fk in _model.EntityTypeForTable(table)!.GetForeignKeys().Where(fk => fk.IsRequired))
        {
            var principal = fk.PrincipalEntityType.GetTableName();
            if (principal is null) continue;
            if (string.Equals(principal, table, StringComparison.OrdinalIgnoreCase)) continue;
            if (!position.TryGetValue(principal, out var at)) continue;
            if (at > position[table]) violations.Add($"{table} loads before its parent {principal}");
        }

        Assert.True(!violations.Any(), string.Join("; ", violations));
    }

    // The guard against loading an artifact across a migration boundary. A column the model gained since
    // generation would otherwise be inserted as its default with nobody told.
    [Fact]
    public void Refuses_an_artifact_whose_columns_do_not_match_the_model()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildPlan("mismatch", dir =>
            RewriteFirstChunk(dir, "ServiceLines", line =>
            {
                var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(line)!;
                o.Remove("Code");
                return JsonSerializer.Serialize(o);
            })));

        Assert.Contains("does not match the model", ex.Message);
        Assert.Contains("Code", ex.Message);
    }

    // Migrations here are additive by convention, so an artifact generated on Monday legitimately lacks
    // Tuesday's new NULLABLE column. Refusing that meant regenerating 3.7 GB, an hour's work, every time
    // any nullable column landed on main. It happened twice in one day (ServiceProviderDate, then
    // ArtifactsAdoptedAtUtc), and the second one refused an artifact generated 30 minutes earlier.
    //
    // Asserting that Build SUCCEEDS is not enough, and the first version of this test made exactly that
    // mistake: it proved the gate opened, not that anything could drive through it. ChunkDataReader treats
    // an absent column as corruption and throws on its first row, so a plan that merely tolerated the gap
    // would pass verification and then die on row 1 of the real load. The binder has to be GONE.
    [Fact]
    public void Tolerates_an_artifact_missing_a_nullable_column()
    {
        var dir = ArtifactTests.Build(Path.Combine(_dir, "nullable-gap"));
        // Description is string? on WorkOrder, so a row without it is exactly a row written before the
        // column existed: the bulk copy does not supply it and it lands NULL.
        RewriteEveryChunk(dir, "WorkOrders", line =>
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(line)!;
            o.Remove("Description");
            return JsonSerializer.Serialize(o);
        });

        var plan = LoadPlan.Build(Artifact.Open(dir), _model);
        var workOrders = plan.Tables.Single(t => t.Table == "WorkOrders");

        // The dropped column carries no binder, so nothing downstream asks a row for it. This is what
        // SqlBulkCopy's column mappings are built from, so its absence here is its absence there.
        Assert.DoesNotContain(workOrders.Binders, b => b.ColumnName == "Description");
        Assert.Contains(workOrders.Binders, b => b.ColumnName == "Number");

        // And the reader actually reads, which is the claim the previous assertion could not make. Every
        // row of every chunk, not just the sampled one the plan looked at.
        foreach (var chunk in workOrders.Chunks)
        {
            using var reader = new ChunkDataReader(chunk.Path, workOrders.Binders);
            var rows = 0;
            while (reader.Read()) rows++;
            Assert.Equal(chunk.Rows, rows);
        }
    }

    // A REQUIRED column is a different matter: the row cannot be written at all without it.
    [Fact]
    public void Refuses_an_artifact_missing_a_required_column()
    {
        var dir = ArtifactTests.Build(Path.Combine(_dir, "required-gap"));
        RewriteEveryChunk(dir, "WorkOrders", line =>
        {
            var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(line)!;
            o.Remove("Number");   // required: non-nullable, and half of a unique index
            return JsonSerializer.Serialize(o);
        });

        var ex = Assert.Throws<InvalidOperationException>(() => LoadPlan.Build(Artifact.Open(dir), _model));

        Assert.Contains("REQUIRES", ex.Message);
        Assert.Contains("Number", ex.Message);
    }

    private static void RewriteEveryChunk(string dir, string table, Func<string, string> transform)
    {
        foreach (var f in Directory.GetFiles(Path.Combine(dir, table), $"{table}-*.jsonl.gz"))
            RewriteChunkLines(f, (line, _) => transform(line));
    }

    [Fact]
    public void Refuses_an_artifact_carrying_a_column_the_model_lacks()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildPlan("extra", dir =>
            RewriteFirstChunk(dir, "ServiceLines", line =>
            {
                var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(line)!;
                o["ColumnFromTheFuture"] = "x";
                return JsonSerializer.Serialize(o);
            })));

        Assert.Contains("ColumnFromTheFuture", ex.Message);
    }

    // Round-trip fidelity of the conversion, checked against the JSON the generator actually wrote rather
    // than against values invented here. Every distinct CLR type in these tables goes through it.
    [Fact]
    public void Reader_converts_every_column_type_back_to_its_provider_clr_type()
    {
        var plan = BuildPlan("types");
        var workOrders = plan.Tables.First(t => t.Table == "WorkOrders");

        using var reader = new ChunkDataReader(workOrders.Chunks[0].Path, workOrders.Binders);
        Assert.True(reader.Read());

        var seen = new HashSet<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            Assert.Equal(i, reader.GetOrdinal(name));

            if (reader.IsDBNull(i)) { Assert.Same(DBNull.Value, reader.GetValue(i)); continue; }

            var value = reader.GetValue(i);
            Assert.IsType(reader.GetFieldType(i), value);
            seen.Add(value.GetType().Name);
        }

        // Guid keys, DateTimeOffset stamps, DateOnly dates, enums as ints, flags as bools, text. If the
        // fixture ever stops covering these the assertion says so rather than the coverage quietly shrinking.
        Assert.Contains("Guid", seen);
        Assert.Contains("DateTimeOffset", seen);
        Assert.Contains("DateOnly", seen);
        Assert.Contains("Int32", seen);
        Assert.Contains("Boolean", seen);
        Assert.Contains("String", seen);
    }

    [Fact]
    public void Reader_streams_exactly_the_rows_the_chunk_holds()
    {
        var plan = BuildPlan("stream");
        var table = plan.Tables.First(t => t.Table == "WorkOrders");
        var chunk = table.Chunks[0];

        using var reader = new ChunkDataReader(chunk.Path, table.Binders);
        long n = 0;
        while (reader.Read()) n++;

        Assert.Equal(chunk.Rows, n);
    }

    // Coercing a missing column to null would insert a wrong row instead of stopping. On 42 million rows
    // "wrong but plausible" is the expensive failure, so this one is deliberately loud.
    [Fact]
    public void Reader_refuses_a_row_that_is_missing_a_column()
    {
        var dir = ArtifactTests.Build(Path.Combine(_dir, "missing"));
        var plan = LoadPlan.Build(Artifact.Open(dir), _model);
        var table = plan.Tables.First(t => t.Table == "ServiceLines");

        // Corrupt the SECOND row, so the plan's own first-row column check passes and the reader is what
        // catches it.
        RewriteChunkLines(table.Chunks[0].Path, (line, index) =>
        {
            if (index != 1) return line;
            var o = JsonSerializer.Deserialize<Dictionary<string, object?>>(line)!;
            o.Remove("Name");
            return JsonSerializer.Serialize(o);
        });

        using var reader = new ChunkDataReader(table.Chunks[0].Path, table.Binders);
        Assert.True(reader.Read());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.Read());
        Assert.Contains("no value for column 'Name'", ex.Message);
    }

    private static void RewriteFirstChunk(string dir, string table, Func<string, string> transform) =>
        RewriteChunkLines(
            Directory.GetFiles(Path.Combine(dir, table), $"{table}-*.jsonl.gz")
                .OrderBy(f => f, StringComparer.Ordinal).First(),
            (line, index) => index == 0 ? transform(line) : line);

    private static void RewriteChunkLines(string path, Func<string, int, string> transform)
    {
        var lines = new List<string>();
        using (var file = File.OpenRead(path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            while (reader.ReadLine() is { } line) lines.Add(line);
        }

        using var outFile = File.Create(path);
        using var outGzip = new GZipStream(outFile, CompressionLevel.Optimal);
        using var writer = new StreamWriter(outGzip);
        for (var i = 0; i < lines.Count; i++) writer.WriteLine(transform(lines[i], i));
    }

    public void Dispose()
    {
        _model.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
