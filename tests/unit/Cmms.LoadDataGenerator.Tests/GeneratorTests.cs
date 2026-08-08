using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cmms.Domain.Auditing;
using Cmms.Domain.Work;
using Cmms.Infrastructure.Auditing;
using Cmms.Infrastructure.Persistence;
using Cmms.Infrastructure.Tenancy;
using Cmms.LoadDataGenerator;
using Microsoft.EntityFrameworkCore;

namespace Cmms.LoadDataGenerator.Tests;

// lnicoara/cmms#2876. Every test here runs with no database, no container and no Azure credential, which
// is the property that makes the generator checkable on a laptop and is the reason generating was split
// from loading in the first place.
public class GeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gen-tests-" + Guid.NewGuid().ToString("N"));

    private GeneratorOptions Small(string outDir) => new()
    {
        Seed = "test-seed", OutDir = outDir,
        WorkOrders = 300, Assets = 120, Users = 6,
        Campuses = 2, BuildingsPerCampus = 2, FloorsPerBuilding = 2, RoomsPerFloor = 3,
        Manufacturers = 5, ModelsPerManufacturer = 3, AssetTypes = 6, Accounts = 4,
    };

    // The contract the whole load test rests on: same seed and parameters, byte-identical artifact. Without
    // it a sizing curve cannot be re-run months later on a different tier and compared to the first one.
    // Asserted across two SEPARATE generator instances over a digest of the decompressed bytes, not a
    // call-twice-compare-in-one-process, which would pass even with process-level state leaking in.
    [Fact]
    public void Same_seed_produces_a_byte_identical_artifact()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        new Generator(Small(a)).Run();
        new Generator(Small(b)).Run();

        Assert.Equal(Digest(a), Digest(b));
    }

    [Fact]
    public void A_different_seed_produces_a_different_artifact()
    {
        var a = Path.Combine(_dir, "c");
        var b = Path.Combine(_dir, "d");
        new Generator(Small(a)).Run();
        var other = Small(b);
        other.Seed = "a-different-seed";
        new Generator(other).Run();

        Assert.NotEqual(Digest(a), Digest(b));
    }

    // Sequential keys would append every insert to one hot page and produce a perfectly packed clustered
    // index no real tenant has, which would make the load test optimistic about page splits and index
    // depth. This fails loudly if anyone ever "optimizes" the id scheme.
    [Fact]
    public void Primary_keys_are_version_4_with_the_rfc_variant()
    {
        var d = new Deterministic("test-seed");
        for (var i = 0; i < 2000; i++)
        {
            var g = d.Id("asset", i).ToString();
            Assert.Equal('4', g[14]);
            Assert.Contains(char.ToLowerInvariant(g[19]), "89ab");
        }
    }

    // Uniformity on the byte SQL Server sorts uniqueidentifier by. If the high-order byte clustered, the
    // inserts would gain locality production does not have.
    [Fact]
    public void Key_bytes_are_spread_across_the_sql_server_ordering_byte()
    {
        var d = new Deterministic("test-seed");
        var seen = new HashSet<byte>();
        for (var i = 0; i < 5000; i++) seen.Add(d.Id("wo", i).ToByteArray()[10]);
        Assert.True(seen.Count > 200, $"only {seen.Count} distinct values of byte 10");
    }

    // The bug this caught for real: EF's convention marks a Guid primary key ValueGenerated.OnAdd even
    // though Entity.Id is assigned in C#, so filtering on "not Never" dropped Id from every row and every
    // foreign key dangled.
    [Fact]
    public void Column_manifest_keeps_the_client_assigned_key_and_drops_the_store_generated_column()
    {
        using var bridge = new ModelBridge();
        var names = bridge.Columns<WorkOrder>().Select(p => p.Name).ToList();

        Assert.Contains("Id", names);
        Assert.DoesNotContain("RowVersion", names);
    }

    [Fact]
    public void Emitted_rows_carry_the_primary_key()
    {
        using var bridge = new ModelBridge();
        var wo = new WorkOrder
        {
            Id = Guid.NewGuid(), ServiceLineId = Guid.NewGuid(), Number = "WO-00001",
            AssetId = Guid.NewGuid(), WorkSummary = "x", CreatedDate = new DateOnly(2026, 8, 4),
        };

        var row = bridge.Row(wo);

        Assert.True(row.ContainsKey("Id"));
        Assert.Equal(wo.Id, row["Id"]);
    }

    // The audit table is the largest in the tenant, so its bytes decide the sizing curve. Producing them
    // through the product's own extracted code rather than a copy is what keeps that honest, and this is
    // the test that proves the copy has not quietly appeared.
    [Fact]
    public void Generated_audit_json_is_byte_identical_to_the_interceptor()
    {
        var wo = new WorkOrder
        {
            Id = Guid.Parse("11111111-2222-4333-8444-555555555555"),
            ServiceLineId = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa"),
            Number = "WO-00042", AssetId = Guid.Parse("bbbbbbbb-cccc-4ddd-8eee-ffffffffffff"),
            WorkSummary = "Byte check", CreatedDate = new DateOnly(2026, 3, 14),
        };

        using var bridge = new ModelBridge();
        var fromGenerator = bridge.CreatedAudit(wo, DateTimeOffset.UnixEpoch, "u-1", "Alice", null).ChangesJson;

        // The same entity through a plain context, diffed by the product's own AuditDiff.
        var options = new DbContextOptionsBuilder<CmmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new CmmsDbContext(options, ServiceLineContext.None);
        var entry = db.Entry(wo);
        entry.State = EntityState.Added;
        var fromProduct = AuditDiff.Build(entry, AuditAction.Created);

        Assert.Equal(fromProduct, fromGenerator);
    }

    // Every audit ChangesJson must parse, because the column carries an ISJSON check constraint and a bulk
    // insert does not validate check constraints by default.
    [Fact]
    public void Every_generated_audit_row_carries_parseable_json()
    {
        var dir = Path.Combine(_dir, "json");
        new Generator(Small(dir)).Run();

        var checkedRows = 0;
        foreach (var row in ReadTable(dir, "AuditEvents"))
        {
            if (!row.TryGetValue("ChangesJson", out var cj) || cj.ValueKind == JsonValueKind.Null) continue;
            using var _ = JsonDocument.Parse(cj.GetString()!);
            checkedRows++;
        }
        Assert.True(checkedRows > 0, "no audit rows were produced");
    }

    // Numbers must be dense 1..N per service line: the app mints the next one as COUNT(*) + 1 over every
    // row in the line, so a gap means the first work order a load-test user creates collides on the unique
    // index and the entire SaveChanges rolls back.
    [Fact]
    public void Work_order_numbers_are_dense_per_service_line()
    {
        var dir = Path.Combine(_dir, "numbers");
        new Generator(Small(dir)).Run();

        var perLine = new Dictionary<string, List<int>>();
        foreach (var row in ReadTable(dir, "WorkOrders"))
        {
            var line = row["ServiceLineId"].GetString()!;
            var n = int.Parse(row["Number"].GetString()!.Split('-')[^1]);
            if (!perLine.TryGetValue(line, out var l)) perLine[line] = l = new List<int>();
            l.Add(n);
        }

        Assert.NotEmpty(perLine);
        foreach (var (line, nums) in perLine)
        {
            nums.Sort();
            Assert.Equal(1, nums[0]);
            Assert.Equal(nums.Count, nums[^1]);
        }
    }

    [Fact]
    public void Self_validation_passes_on_a_freshly_generated_artifact()
    {
        var dir = Path.Combine(_dir, "validate");
        new Generator(Small(dir)).Run();

        var result = Validator.Run(dir);

        Assert.True(result.Ok, string.Join("\n", result.Lines));
    }

    // lnicoara/cmms#2965. Twenty-one of the validator's checks are violation COUNTERS, and a table that was
    // never written produces the same "0 violations" a correct one does. So an artifact missing a table
    // validated green while containing nothing, and this is the check that makes the other twenty-one mean
    // something. Deleting a whole table's chunks is exactly what a zeroed cardinality option produced.
    [Fact]
    public void Validation_fails_when_an_expected_table_is_missing()
    {
        var dir = Path.Combine(_dir, "vacuous-missing");
        new Generator(Small(dir)).Run();
        Assert.True(Validator.Run(dir).Ok, "the artifact must be valid before the table is removed");

        Directory.Delete(Path.Combine(dir, "Accounts"), recursive: true);

        var result = Validator.Run(dir);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.Contains("FAIL") && l.Contains("present and non-empty"));
        Assert.Contains(result.Lines, l => l.Contains("Accounts"));
    }

    // Same failure wearing different clothes: the chunk file exists and decompresses, it just holds no rows.
    [Fact]
    public void Validation_fails_when_an_expected_table_is_empty()
    {
        var dir = Path.Combine(_dir, "vacuous-empty");
        new Generator(Small(dir)).Run();

        var chunk = Directory.GetFiles(Path.Combine(dir, "Accounts"), "Accounts-*.jsonl.gz").Single();
        using (var f = File.Create(chunk))
        using (var gz = new GZipStream(f, CompressionMode.Compress))
        {
            gz.Write(ReadOnlySpan<byte>.Empty);
        }

        var result = Validator.Run(dir);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.Contains("FAIL") && l.Contains("present and non-empty"));
    }

    // AccessGroup.LocationIdsJson has NO foreign key in any tenant migration and nothing checks it at load
    // time. AccessGroupScopeResolver drops a pin it cannot resolve and leaves the group pinned but scoped to
    // nothing, so the members see an empty application and the LocationAccess scan never fires.
    [Fact]
    public void Validation_fails_when_a_location_pin_resolves_to_nothing()
    {
        var dir = Path.Combine(_dir, "dangling-pin");
        new Generator(Small(dir)).Run();
        Assert.True(Validator.Run(dir).Ok, "the artifact must be valid before the pin is broken");

        RewriteTable(dir, "AccessGroups", line =>
        {
            var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            var pins = row.TryGetValue("LocationIdsJson", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(pins) || pins == "[]") return line;
            // A well-formed Guid that was never emitted as a location.
            var replaced = JsonSerializer.SerializeToElement($"[\"{Guid.NewGuid()}\"]");
            row["LocationIdsJson"] = replaced;
            return JsonSerializer.Serialize(row);
        });

        var result = Validator.Run(dir);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.Contains("FAIL") && l.Contains("pin resolves"));
    }

    // Found by review of the first version of this change, which "tolerantly" parsed LocationIdsJson and
    // returned zero pins on malformed input. That reintroduced the very defect the rest of this work
    // removes: the group still counted as pinned, contributed no pins, and the reachability check passes
    // when there is nothing to reach, so a corrupt group validated GREEN. Production treats corrupt pin
    // JSON as deny-all, so the pinned persona would have measured an empty application.
    [Theory]
    [InlineData("{{{not json")]
    [InlineData("{\"not\":\"an array\"}")]
    [InlineData("[123]")]
    [InlineData("[\"not-a-guid\"]")]
    public void Validation_fails_when_a_location_pin_list_is_malformed(string corrupt)
    {
        var dir = Path.Combine(_dir, "malformed-pin-" + Math.Abs(corrupt.GetHashCode()));
        new Generator(Small(dir)).Run();
        Assert.True(Validator.Run(dir).Ok, "the artifact must be valid before the pins are corrupted");

        RewriteTable(dir, "AccessGroups", line =>
        {
            var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            var pins = row.TryGetValue("LocationIdsJson", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(pins) || pins == "[]") return line;
            row["LocationIdsJson"] = JsonSerializer.SerializeToElement(corrupt);
            return JsonSerializer.Serialize(row);
        });

        var result = Validator.Run(dir);

        Assert.False(result.Ok, string.Join("\n", result.Lines));
    }

    // A group that declares itself pinned and yields no pins is scoped to nothing, which is the same
    // deny-all outcome as corrupt JSON and just as invisible to a counter-based check.
    [Fact]
    public void Validation_fails_when_a_pinned_group_has_an_empty_pin_list()
    {
        var dir = Path.Combine(_dir, "empty-pin-list");
        new Generator(Small(dir)).Run();

        RewriteTable(dir, "AccessGroups", line =>
        {
            var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!;
            var pins = row.TryGetValue("LocationIdsJson", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(pins) || pins == "[]") return line;
            // Non-empty and not "[]", so it still counts as a pinned group, but parses to nothing.
            row["LocationIdsJson"] = JsonSerializer.SerializeToElement("[ ]");
            return JsonSerializer.Serialize(row);
        });

        var result = Validator.Run(dir);

        Assert.False(result.Ok, string.Join("\n", result.Lines));
    }

    // An empty parent pool is what a zeroed cardinality option actually causes. It used to surface as
    // IndexOutOfRangeException from items[0], naming neither the pool nor the option, and only after the
    // output directory had already been deleted.
    [Fact]
    public void An_empty_parent_pool_fails_with_a_message_naming_the_draw()
    {
        var rng = new Deterministic("test-seed");

        var ex = Assert.Throws<InvalidOperationException>(
            () => rng.Pick("asset", 0, "type", Array.Empty<Guid>()));

        Assert.Contains("parent pool is empty", ex.Message);
        Assert.Contains("type", ex.Message);
    }

    // The small profile exists so the whole chain is provable in minutes, and it is only safe to load if its
    // keys cannot be confused with the full artifact's. Keys are a pure function of (seed, kind, ordinal)
    // with no count input, so an artifact generated on the SAME seed yields a strict subset of the full
    // artifact's keys, and the loader's chunk-presence probe is a bare primary-key lookup.
    [Fact]
    public void The_small_profile_seed_shares_no_keys_with_the_full_profile()
    {
        var full = new Deterministic("cmms-loadtest-v1");
        var small = new Deterministic("cmms-loadtest-small-v1");

        for (long ordinal = 0; ordinal < 2_000; ordinal++)
        {
            Assert.NotEqual(full.Id("wo", ordinal), small.Id("wo", ordinal));
            Assert.NotEqual(full.Id("asset", ordinal), small.Id("asset", ordinal));
            Assert.NotEqual(full.Id("loc", ordinal), small.Id("loc", ordinal));
        }
    }

    // Every shipped profile must be loadable and internally consistent, which means a distinct seed and no
    // zeroed cardinality. smoke.json shipped with BOTH defects: it reused cmms-loadtest-v1 and scaled the
    // reference tier down to the point where the dataset stopped being representative.
    [Theory]
    [InlineData("small.json")]
    [InlineData("smoke.json")]
    [InlineData("full.json")]
    public void Shipped_profiles_are_internally_valid(string profile)
    {
        var path = Path.Combine(ProfileDirectory(), profile);
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;

        var seed = json["Seed"].GetString();
        Assert.False(string.IsNullOrWhiteSpace(seed));

        foreach (var key in new[]
                 {
                     "WorkOrders", "Assets", "Users", "Campuses", "BuildingsPerCampus", "FloorsPerBuilding",
                     "RoomsPerFloor", "Manufacturers", "ModelsPerManufacturer", "AssetTypes", "Accounts",
                 })
        {
            Assert.True(json.ContainsKey(key), $"{profile} is missing {key}");
            Assert.True(json[key].GetInt32() > 0, $"{profile} has a non-positive {key}");
        }

        // Distinct seeds across every profile, so no two artifacts can be mistaken for each other.
        var seeds = Directory.GetFiles(ProfileDirectory(), "*.json")
            .Select(f => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(f)))
            .Where(d => d is not null && d.ContainsKey("Seed"))
            .Select(d => d!["Seed"].GetString())
            .ToList();
        Assert.Equal(seeds.Count, seeds.Distinct().Count());
    }

    /// <summary>
    /// The one directory the profiles live in. lnicoara/cmms#3026.
    ///
    /// This used to walk up to tools/Cmms.LoadDataGenerator, because the profiles sat beside the generator
    /// in cmms AND in profiles/ here, byte-identical, with the generator reading the copy nobody called
    /// authoritative. There is one copy now, and this reads it rather than a copy of it, so a profile
    /// edited in the wrong place fails a test instead of quietly generating a different dataset.
    /// </summary>
    private static string ProfileDirectory()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "profiles")))
            d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "profiles");
    }

    private static void RewriteTable(string dir, string table, Func<string, string> transform)
    {
        foreach (var path in Directory.GetFiles(Path.Combine(dir, table), $"{table}-*.jsonl.gz"))
        {
            List<string> lines = [];
            using (var read = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            using (var reader = new StreamReader(read))
                while (reader.ReadLine() is { } line)
                    lines.Add(transform(line));

            using var write = new GZipStream(File.Create(path), CompressionMode.Compress);
            using var writer = new StreamWriter(write);
            foreach (var line in lines) writer.WriteLine(line);
        }
    }

    // A member with no LaborId turns the technician's own work-order query into WHERE 1=0, so the persona
    // that is most of the intended load would measure an empty result set.
    [Fact]
    public void Every_generated_member_is_linked_to_a_labor_row()
    {
        var dir = Path.Combine(_dir, "users");
        new Generator(Small(dir)).Run();

        var users = ReadTable(dir, "Users").ToList();
        Assert.NotEmpty(users);
        Assert.All(users, u => Assert.NotEqual(JsonValueKind.Null, u["LaborId"].ValueKind));
    }

    // The declared shares and the skew have to be consistent with each other, not merely each plausible on
    // its own. #2880 predicts roughly 6.5 audit rows per work order.
    [Fact]
    public void Audit_rows_per_work_order_land_near_the_specified_mean()
    {
        var dir = Path.Combine(_dir, "mean");
        var stats = new Generator(Small(dir)).Run();

        var perWo = (double)stats.WorkOrderAuditRows / 300;
        Assert.InRange(perWo, 5.0, 8.5);
    }

    // Regression guards for the three defects the PR review caught. Each one produced an artifact that
    // looked fine and was wrong in a way only a load attempt or a misleading benchmark would reveal.

    // CreatedAtUtc is NOT NULL with no database default, so a row without it fails the insert outright.
    // A soft-deleted row without DeletedAtUtc is worse: the database accepts it and the audit trail lies.
    [Fact]
    public void Rows_carry_the_provenance_the_interceptor_would_have_written()
    {
        var dir = Path.Combine(_dir, "provenance");
        new Generator(Small(dir)).Run();

        var rows = ReadTable(dir, "WorkOrders").ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotEqual(JsonValueKind.Null, r["CreatedAtUtc"].ValueKind));
        Assert.All(rows, r => Assert.NotEqual(JsonValueKind.Null, r["CreatedBy"].ValueKind));

        var deleted = rows.Where(r => r["IsDeleted"].GetBoolean()).ToList();
        Assert.NotEmpty(deleted);
        Assert.All(deleted, r => Assert.NotEqual(JsonValueKind.Null, r["DeletedAtUtc"].ValueKind));
    }

    // The audit table is the largest in the tenant, so a hand-written diff naming invented fields would
    // put the entire sizing curve on fiction. Every key must be a property the entity actually has.
    [Fact]
    public void Update_audit_diffs_name_only_real_entity_properties()
    {
        var dir = Path.Combine(_dir, "updates");
        new Generator(Small(dir)).Run();

        using var bridge = new ModelBridge();
        var valid = bridge.AllPropertyNames<WorkOrder>();

        var updates = 0;
        foreach (var row in ReadTable(dir, "AuditEvents"))
        {
            if (row["EntityType"].GetString() != nameof(WorkOrder)) continue;
            if (row["Action"].GetInt32() != (int)AuditAction.Updated) continue;
            if (row["ChangesJson"].ValueKind == JsonValueKind.Null) continue;

            using var doc = JsonDocument.Parse(row["ChangesJson"].GetString()!);
            foreach (var prop in doc.RootElement.EnumerateObject())
                Assert.Contains(prop.Name, valid);
            updates++;
        }
        Assert.True(updates > 0, "no update audit rows were produced");
    }

    // An update diff records old and new, unlike a create which carries only new. Getting this wrong
    // halves the measured byte size of the biggest table.
    [Fact]
    public void Update_audit_diffs_carry_old_and_new_values()
    {
        var dir = Path.Combine(_dir, "oldnew");
        new Generator(Small(dir)).Run();

        var sawOldNew = false;
        foreach (var row in ReadTable(dir, "AuditEvents"))
        {
            if (row["Action"].GetInt32() != (int)AuditAction.Updated) continue;
            if (row["ChangesJson"].ValueKind == JsonValueKind.Null) continue;

            using var doc = JsonDocument.Parse(row["ChangesJson"].GetString()!);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                Assert.True(prop.Value.TryGetProperty("old", out _), $"{prop.Name} has no old value");
                Assert.True(prop.Value.TryGetProperty("new", out _), $"{prop.Name} has no new value");
                sawOldNew = true;
            }
            if (sawOldNew) break;
        }
        Assert.True(sawOldNew, "no update diff was inspected");
    }

    // A DefaultAccessGroupId pointing at a group that does not exist resolves as unrestricted, so the
    // LocationAccess full-equipment scan silently never fires and the run measures the easy path.
    [Fact]
    public void Access_groups_exist_and_at_least_one_carries_location_pins()
    {
        var dir = Path.Combine(_dir, "groups");
        new Generator(Small(dir)).Run();

        var groups = ReadTable(dir, "AccessGroups").ToList();
        Assert.NotEmpty(groups);

        var ids = groups.Select(g => g["Id"].GetString()!).ToHashSet();
        var pinned = groups.Count(g => g["LocationIdsJson"].ValueKind != JsonValueKind.Null
                                       && g["LocationIdsJson"].GetString() != "[]");
        Assert.True(pinned > 0, "no access group carries location pins");

        var members = ReadTable(dir, "Users")
            .Where(u => u["DefaultAccessGroupId"].ValueKind != JsonValueKind.Null)
            .ToList();
        Assert.NotEmpty(members);
        Assert.All(members, u => Assert.Contains(u["DefaultAccessGroupId"].GetString()!, ids));
    }

    // Second review round. The generator had no coherent notion of a record's life: audit rows were dated
    // independently of the entity, so edits landed before the record existed, soft deletes produced no
    // Deleted audit row at all, and the persisted status contradicted its own trail. All three load
    // without complaint and quietly poison anything built on the audit history.

    [Fact]
    public void No_audit_row_predates_the_record_it_describes()
    {
        var dir = Path.Combine(_dir, "timeline");
        new Generator(Small(dir)).Run();

        var born = ReadTable(dir, "WorkOrders")
            .ToDictionary(r => r["Id"].GetString()!, r => DateTimeOffset.Parse(r["CreatedAtUtc"].GetString()!));

        var checkedRows = 0;
        foreach (var a in ReadTable(dir, "AuditEvents"))
        {
            if (a["EntityType"].GetString() != nameof(WorkOrder)) continue;
            if (!born.TryGetValue(a["EntityId"].GetString()!, out var created)) continue;

            var when = DateTimeOffset.Parse(a["OccurredAtUtc"].GetString()!);
            Assert.True(when >= created, $"audit at {when:o} predates creation at {created:o}");
            checkedRows++;
        }
        Assert.True(checkedRows > 0, "no work-order audit rows were inspected");
    }

    [Fact]
    public void Every_soft_deleted_record_has_a_deleted_audit_row()
    {
        var dir = Path.Combine(_dir, "deletes");
        new Generator(Small(dir)).Run();

        var deleted = ReadTable(dir, "WorkOrders")
            .Where(r => r["IsDeleted"].GetBoolean())
            .Select(r => r["Id"].GetString()!)
            .ToHashSet();

        var withDeleteRow = ReadTable(dir, "AuditEvents")
            .Where(a => a["EntityType"].GetString() == nameof(WorkOrder)
                        && a["Action"].GetInt32() == (int)AuditAction.Deleted)
            .Select(a => a["EntityId"].GetString()!)
            .ToHashSet();

        Assert.NotEmpty(deleted);
        Assert.All(deleted, id => Assert.Contains(id, withDeleteRow));
    }

    // A Deleted diff is exactly the soft-delete transition, never the whole entity.
    [Fact]
    public void Deleted_audit_diffs_record_only_the_soft_delete_transition()
    {
        var dir = Path.Combine(_dir, "deldiff");
        new Generator(Small(dir)).Run();

        var seen = 0;
        foreach (var a in ReadTable(dir, "AuditEvents"))
        {
            if (a["Action"].GetInt32() != (int)AuditAction.Deleted) continue;
            Assert.Equal("{\"IsDeleted\":{\"old\":false,\"new\":true}}", a["ChangesJson"].GetString());
            seen++;
        }
        Assert.True(seen > 0, "no delete audit rows were produced");
    }

    // Nothing may be dated after the generation date, or the tenant holds records from its own future.
    [Fact]
    public void No_timestamp_lands_after_the_generation_date()
    {
        var dir = Path.Combine(_dir, "future");
        var opts = Small(dir);
        new Generator(opts).Run();

        var cutoff = new DateTimeOffset(opts.GenerationDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        foreach (var r in ReadTable(dir, "WorkOrders"))
            foreach (var col in new[] { "CreatedAtUtc", "ModifiedAtUtc", "DeletedAtUtc" })
                if (r[col].ValueKind != JsonValueKind.Null)
                    Assert.True(DateTimeOffset.Parse(r[col].GetString()!) <= cutoff, $"{col} is future-dated");
    }

    // The manifest is part of the artifact, so the same seed written to two different directories must
    // produce two identical manifests. OutDir is where it sits, not what it contains.
    [Fact]
    public void The_manifest_does_not_depend_on_the_output_directory()
    {
        var a = Path.Combine(_dir, "m1");
        var b = Path.Combine(_dir, "m2");
        new Generator(Small(a)).Run();
        new Generator(Small(b)).Run();

        // The generator itself does not write the manifest (Program does), so assert the property that
        // matters here: no emitted content varies with the path.
        Assert.Equal(Digest(a), Digest(b));
    }

    // Third review round. The audit trail existed but could not be replayed onto the row it described:
    // the persisted status was one no audit row ever recorded, timestamps collapsed for records created
    // on the generation date, and ModifiedBy named a different person than the last audit row.

    [Fact]
    public void Persisted_status_is_the_one_the_audit_trail_last_recorded()
    {
        var dir = Path.Combine(_dir, "replay");
        new Generator(Small(dir)).Run();

        var lastStatus = new Dictionary<string, (DateTimeOffset At, int Status)>();
        foreach (var a in ReadTable(dir, "AuditEvents"))
        {
            if (a["EntityType"].GetString() != nameof(WorkOrder)) continue;
            if (a["ChangesJson"].ValueKind == JsonValueKind.Null) continue;

            using var doc = JsonDocument.Parse(a["ChangesJson"].GetString()!);
            if (!doc.RootElement.TryGetProperty("Status", out var st)) continue;
            if (!st.TryGetProperty("new", out var nv) || nv.ValueKind != JsonValueKind.Number) continue;

            var id = a["EntityId"].GetString()!;
            var at = DateTimeOffset.Parse(a["OccurredAtUtc"].GetString()!);
            if (!lastStatus.TryGetValue(id, out var prev) || at >= prev.At)
                lastStatus[id] = (at, nv.GetInt32());
        }

        var compared = 0;
        foreach (var w in ReadTable(dir, "WorkOrders"))
        {
            if (!lastStatus.TryGetValue(w["Id"].GetString()!, out var la)) continue;
            Assert.Equal(la.Status, w["Status"].GetInt32());
            compared++;
        }
        Assert.True(compared > 0, "no work order was compared against its trail");
    }

    [Fact]
    public void Audit_timestamps_strictly_increase_within_a_record()
    {
        var dir = Path.Combine(_dir, "monotonic");
        new Generator(Small(dir)).Run();

        var byId = new Dictionary<string, List<DateTimeOffset>>();
        foreach (var a in ReadTable(dir, "AuditEvents"))
        {
            if (a["EntityType"].GetString() != nameof(WorkOrder)) continue;
            var id = a["EntityId"].GetString()!;
            if (!byId.TryGetValue(id, out var l)) byId[id] = l = new List<DateTimeOffset>();
            l.Add(DateTimeOffset.Parse(a["OccurredAtUtc"].GetString()!));
        }

        Assert.NotEmpty(byId);
        foreach (var (id, times) in byId)
        {
            times.Sort();
            for (var i = 1; i < times.Count; i++)
                Assert.True(times[i] > times[i - 1], $"{id} has two events at {times[i]:o}");
        }
    }

    [Fact]
    public void Modified_by_names_whoever_made_the_last_recorded_change()
    {
        var dir = Path.Combine(_dir, "actor");
        new Generator(Small(dir)).Run();

        var lastActor = new Dictionary<string, (DateTimeOffset At, string? Actor)>();
        foreach (var a in ReadTable(dir, "AuditEvents"))
        {
            if (a["EntityType"].GetString() != nameof(WorkOrder)) continue;
            var id = a["EntityId"].GetString()!;
            var at = DateTimeOffset.Parse(a["OccurredAtUtc"].GetString()!);
            if (!lastActor.TryGetValue(id, out var prev) || at >= prev.At)
                lastActor[id] = (at, a["ActorName"].GetString());
        }

        var compared = 0;
        foreach (var w in ReadTable(dir, "WorkOrders"))
        {
            if (w["ModifiedAtUtc"].ValueKind == JsonValueKind.Null) continue;
            if (!lastActor.TryGetValue(w["Id"].GetString()!, out var la) || la.Actor is null) continue;
            Assert.Equal(la.Actor, w["ModifiedBy"].GetString());
            compared++;
        }
        Assert.True(compared > 0, "no work order was compared");
    }

    // THE v1 FORMAT CONTRACT, pinned. lnicoara/cmms#2908 grill round 5.
    //
    // The loader derives exact per-chunk row counts from a directory listing plus ONE file read per table
    // instead of decompressing all 476 chunks, and that derivation is only valid because every chunk except
    // a table's last is exactly full. This test is what stops that becoming untrue by accident: a writer
    // that rolled on compressed size, or on a timer, would still produce a perfectly valid-looking artifact
    // and would silently corrupt the loader's expected-row-count preflight. Changing the roll rule is a v2
    // format change, and this test is where that decision gets made deliberately.
    [Fact]
    public void Every_chunk_except_a_tables_last_is_exactly_full()
    {
        var dir = Path.Combine(_dir, "v1-contract");
        var options = Small(dir);
        options.RowsPerChunk = 29;   // small enough that every table lands several chunks
        new Generator(options).Run();

        var checkedTables = 0;
        foreach (var tableDir in Directory.GetDirectories(dir))
        {
            var table = Path.GetFileName(tableDir);
            var chunks = Directory.GetFiles(tableDir, $"{table}-*.jsonl.gz")
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (chunks.Count < 2) continue;

            foreach (var chunk in chunks.Take(chunks.Count - 1))
                Assert.Equal(options.RowsPerChunk, CountLines(chunk));

            Assert.InRange(CountLines(chunks[^1]), 1, options.RowsPerChunk);
            checkedTables++;
        }

        Assert.True(checkedTables > 0, "the fixture must produce at least one multi-chunk table");
    }

    private static int CountLines(string gzPath)
    {
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        var n = 0;
        while (sr.ReadLine() is not null) n++;
        return n;
    }

    private static string Digest(string dir)
    {
        using var sha = SHA256.Create();
        foreach (var f in Directory.GetFiles(dir, "*.jsonl.gz", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(f);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var buf = new byte[81920];
            int read;
            while ((read = gz.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static IEnumerable<Dictionary<string, JsonElement>> ReadTable(string dir, string table)
    {
        var d = Path.Combine(dir, table);
        if (!Directory.Exists(d)) yield break;
        foreach (var f in Directory.GetFiles(d, "*.jsonl.gz").OrderBy(x => x, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(f);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (row is not null) yield return row;
            }
        }
    }

    // lnicoara/cmms#2969. A profile the operator NAMED and that cannot be found must stop the run. The
    // fallback is not a harmless smaller dataset: GeneratorOptions defaults carry the FULL profile's seed,
    // so the artifact would share a key space with the full one, and the loader's chunk-presence probe is a
    // bare primary-key lookup that would adopt the wrong rows and checkpoint them undeletably.
    [Fact]
    public void An_unresolvable_profile_request_is_refused()
    {
        var result = ProfileResolver.Resolve("nope.json", [_dir]);

        Assert.False(result.Ok);
        Assert.Null(result.Path);
        Assert.Contains("nope.json", result.Error);
        // The message must name the consequence, not just the missing file, because the operator's next
        // instinct on "file not found" is to shrug and rerun.
        Assert.Contains("cmms-loadtest-v1", result.Error);
    }

    // No profile named is not an error. A bare run on defaults plus params.json is a legitimate path, and
    // the distinction that matters is between what was asked for and what merely might have been there.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_profile_request_is_not_an_error(string? requested)
    {
        var result = ProfileResolver.Resolve(requested, [_dir]);

        Assert.True(result.Ok);
        Assert.Null(result.Path);
    }

    // The reason a naive File.Exists check was not enough. ConfigurationBuilder resolves a relative path
    // from AppContext.BaseDirectory (the bin output, where the csproj copies the profiles) while File.Exists
    // resolves from the working directory. A profile reachable from EITHER root must resolve, and the result
    // must be absolute so the builder cannot then look somewhere else and quietly find nothing.
    [Fact]
    public void A_profile_is_found_under_any_supplied_root_and_returned_absolute()
    {
        var rootA = Path.Combine(_dir, "bin-like");
        var rootB = Path.Combine(_dir, "cwd-like");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(Path.Combine(rootB, "tools", "Cmms.LoadDataGenerator"));

        var nested = Path.Combine(rootB, "tools", "Cmms.LoadDataGenerator", "small.json");
        File.WriteAllText(nested, SeedProfile);

        // Present only under the SECOND root, which is exactly the case that used to fall through.
        var result = ProfileResolver.Resolve(
            Path.Combine("tools", "Cmms.LoadDataGenerator", "small.json"), [rootA, rootB]);

        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Path);
        Assert.True(Path.IsPathRooted(result.Path), "the resolved path must be absolute");
        Assert.Equal(Path.GetFullPath(nested), result.Path);
    }

    // First root wins, so the search order is a decision rather than an accident.
    [Fact]
    public void The_first_root_holding_the_profile_wins()
    {
        var first = Path.Combine(_dir, "first");
        var second = Path.Combine(_dir, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "p.json"), SeedProfile);
        File.WriteAllText(Path.Combine(second, "p.json"), SeedProfile);

        var result = ProfileResolver.Resolve("p.json", [first, second]);

        Assert.Equal(Path.GetFullPath(Path.Combine(first, "p.json")), result.Path);
    }

    // Found by review of the first version of this fix, which checked only that the file EXISTED. A profile
    // that is present but inert binds nothing, so the run keeps Seed="cmms-loadtest-v1" and lands in exactly
    // the poisoned state a missing file produces. Same failure, different route.
    [Theory]
    [InlineData("{}", "does not declare a Seed")]
    [InlineData("{\"Sed\":\"typo\"}", "does not declare a Seed")]
    [InlineData("{\"Seed\":\"\"}", "blank or non-string")]
    [InlineData("{\"Seed\":\"   \"}", "blank or non-string")]
    [InlineData("{\"Seed\":123}", "blank or non-string")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("{ not json", "not valid JSON")]
    public void A_named_profile_that_does_not_set_its_own_seed_is_refused(string content, string expected)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"inert-{Math.Abs(content.GetHashCode())}.json");
        File.WriteAllText(path, content);

        var result = ProfileResolver.Resolve(Path.GetFileName(path), [_dir]);

        Assert.False(result.Ok, $"'{content}' should be refused");
        Assert.Contains(expected, result.Error);
        // The message must say why it matters, not merely what is missing.
        Assert.Contains("cmms-loadtest-v1", result.Error);
    }

    // A profile that does set its seed is accepted, so the guard above cannot reject working profiles.
    // Case-insensitive because the configuration binder is; a stricter check would break valid files.
    [Theory]
    [InlineData("{\"Seed\":\"cmms-loadtest-small-v1\"}")]
    [InlineData("{\"seed\":\"lowercase-key-binds-fine\"}")]
    public void A_named_profile_that_sets_its_seed_is_accepted(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"good-{Math.Abs(content.GetHashCode())}.json");
        File.WriteAllText(path, content);

        var result = ProfileResolver.Resolve(Path.GetFileName(path), [_dir]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.GetFullPath(path), result.Path);
    }

    // Every shipped profile must pass its own guard, or the fix breaks the thing it protects.
    [Theory]
    [InlineData("full.json")]
    [InlineData("small.json")]
    [InlineData("smoke.json")]
    public void Every_shipped_profile_declares_its_own_seed(string profile)
    {
        var result = ProfileResolver.Resolve(profile, [AppContext.BaseDirectory]);

        Assert.True(result.Ok, result.Error);
    }

    // An absolute request is honoured as given rather than joined onto a root.
    [Fact]
    public void An_absolute_profile_path_is_used_as_given()
    {
        Directory.CreateDirectory(_dir);
        var abs = Path.Combine(_dir, "abs.json");
        File.WriteAllText(abs, SeedProfile);

        var result = ProfileResolver.Resolve(abs, ["/nonexistent-root"]);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.GetFullPath(abs), result.Path);
    }

    // lnicoara/cmms#2993. The artifact must not claim values that already exist in the target, and the
    // ServiceLines rows were the only place it did.
    //
    // Two collisions, one table. The Id was ServiceLineSeed.FacilitiesId, which is not merely demo data: a
    // MIGRATION inserts 3c4f1c00-0000-4000-8000-000000000001 into EVERY tenant database as the row named
    // "Main" (20260619024138_AddServiceLineScoping). So the artifact carried a primary key present in a
    // brand-new, never-seeded tenant, and every "load into a dedicated unseeded tenant" instruction in this
    // tooling was wrong. The Code was "CE"/"FE", the codes DemoDataSeeder writes, against a UNIQUE index
    // (IX_ServiceLines_Code), so a seeded tenant collided a second time even once the key was unique.
    //
    // Emptying the tenant was the answer to a problem the generator was creating. This is the test that
    // stops it being recreated.
    [Fact]
    public void Emitted_service_lines_claim_no_id_or_code_that_already_exists_in_a_tenant()
    {
        Directory.CreateDirectory(_dir);
        new Generator(Small(_dir)).Run();

        var rows = ReadTable(_dir, "ServiceLines").ToList();
        Assert.Equal(2, rows.Count);

        var ids = rows.Select(r => r["Id"].GetGuid()).ToList();
        var codes = rows.Select(r => r["Code"].GetString()).ToList();

        // Seeded by DemoDataSeeder, and the first of them by a migration into every tenant.
        Assert.DoesNotContain(Cmms.Domain.Organization.ServiceLineSeed.FacilitiesId, ids);
        Assert.DoesNotContain(Cmms.Domain.Organization.ServiceLineSeed.ClinicalEngineeringId, ids);
        // Named literally as well as by the constant. The constant could be changed to match a moved
        // artifact, which would leave this test green while the migration's row is untouched.
        Assert.DoesNotContain(new Guid("3c4f1c00-0000-4000-8000-000000000001"), ids);

        Assert.DoesNotContain("CE", codes);
        Assert.DoesNotContain("FE", codes);
        Assert.DoesNotContain("MAIN", codes);

        // HasMaxLength(32) on Code, so a tag that pushes past it would fail the bulk copy rather than the
        // index. Cheap to check here and expensive to discover in a container job.
        Assert.All(codes, c => Assert.InRange(c!.Length, 1, 32));
    }

    // And they are the DATASET'S values, not one fixed alternative. Two seeds that shared a service-line Id
    // would collide with each other, so stacking a second dataset onto a tenant would fail the same way the
    // seeded values did.
    [Fact]
    public void Two_seeds_produce_different_service_line_ids_and_codes()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        var optionsA = Small(a);
        var optionsB = Small(b);
        optionsB.Seed = optionsA.Seed + "-different";
        new Generator(optionsA).Run();
        new Generator(optionsB).Run();

        var idsA = ReadTable(a, "ServiceLines").Select(r => r["Id"].GetGuid()).ToList();
        var idsB = ReadTable(b, "ServiceLines").Select(r => r["Id"].GetGuid()).ToList();
        var codesA = ReadTable(a, "ServiceLines").Select(r => r["Code"].GetString()).ToList();
        var codesB = ReadTable(b, "ServiceLines").Select(r => r["Code"].GetString()).ToList();

        Assert.Empty(idsA.Intersect(idsB));
        Assert.Empty(codesA.Intersect(codesB));
    }

    // Minimal profile content for the path-resolution tests. It must declare a Seed, because a profile
    // that does not is refused, and these tests are about WHERE a profile is found, not what is in it.
    private const string SeedProfile = "{\"Seed\":\"test-resolver-seed\"}";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
