using System.IO.Compression;
using System.Text.Json;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Reads the finished artifact back off disk and checks the invariants the database would enforce.
///
/// This runs here, not at load time, because SqlBulkCopy does not validate CHECK or FOREIGN KEY
/// constraints by default. Without it a violation surfaces hours into a multi-gigabyte load, or worse it
/// loads silently and every later reader trips over it. Catching it against files, in seconds, is most of
/// the reason generating and loading are separate steps.
///
/// Streaming by construction, for the same reason the writer is: at full scale the artifact holds tens of
/// millions of rows, and an earlier version that materialized them with ToList() pushed the process past
/// 2 GB on a run a fortieth that size. Only the identity sets are retained, as 16-byte Guids rather than
/// 36-character strings.
/// </summary>
public static class Validator
{
    public sealed record Result(bool Ok, List<string> Lines);

    public static Result Run(string dir)
    {
        var lines = new List<string> { "self-validation:" };
        var ok = true;

        void Check(string name, bool passed, string detail = "")
        {
            lines.Add($"  [{(passed ? "ok" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!passed) ok = false;
        }

        // Pass 0: NOTHING MAY PASS VACUOUSLY.
        //
        // Every other check below counts violations and asserts the count is zero, which is exactly what an
        // ABSENT or EMPTY table also produces. Twenty-one of the twenty-two report "0 violations" on a table
        // that was never written, so an artifact missing a table validates green while containing nothing.
        // This pass is what makes the rest of them mean anything, and it runs first so a truncated artifact
        // is named as truncated rather than as valid. lnicoara/cmms#2965
        var emptyOrMissing = new List<string>();
        foreach (var table in ExpectedTables)
        {
            var dirPath = Path.Combine(dir, table);
            if (!Directory.Exists(dirPath) || Directory.GetFiles(dirPath, $"{table}-*.jsonl.gz").Length == 0)
            {
                emptyOrMissing.Add($"{table} (no chunks)");
                continue;
            }
            if (!Read(dir, table).Any()) emptyOrMissing.Add($"{table} (0 rows)");
        }
        Check("every expected table is present and non-empty", emptyOrMissing.Count == 0,
            emptyOrMissing.Count == 0
                ? $"{ExpectedTables.Length} tables"
                : string.Join(", ", emptyOrMissing));

        // Pass 1: assets. Collect ids and parents, check the location constraint and tag uniqueness.
        var assetIds = new HashSet<Guid>();
        var parentIds = new HashSet<Guid>();
        var assetTags = new HashSet<string>(StringComparer.Ordinal);
        var locationParent = new Dictionary<Guid, Guid?>();
        var roomsWithEquipment = new HashSet<Guid>();
        long assetRows = 0, ckViolations = 0, tagDupes = 0;
        foreach (var a in Read(dir, "Assets"))
        {
            assetRows++;
            if (Guid.TryParse(Str(a, "Id"), out var id)) assetIds.Add(id);
            if (Guid.TryParse(Str(a, "ParentId"), out var pid)) parentIds.Add(pid);

            // Retained for the access-group checks below, and deliberately NOT a map of every asset. The
            // location tree is 12,584 rows at full scale against 1,000,000 equipment, and the rooms that
            // hold equipment are bounded by the room count, so both stay small while the pass stays
            // streaming. A parent map over all assets would put a million entries in memory to answer a
            // question about a few thousand.
            if (Num(a, "Kind") == 2 && Guid.TryParse(Str(a, "Id"), out var locId))
                locationParent[locId] = Guid.TryParse(Str(a, "ParentId"), out var lp) ? lp : null;
            else if (Num(a, "Kind") == 1 && Guid.TryParse(Str(a, "ParentId"), out var room))
                roomsWithEquipment.Add(room);
            // CK_Asset_Location_Not_InStorage: [State] IS NULL OR NOT ([Kind] = 2 AND [State] = 3)
            if (Num(a, "Kind") == 2 && Num(a, "State") == 3) ckViolations++;
            if (!assetTags.Add($"{Str(a, "ServiceLineId")}|{Str(a, "AssetTag")}")) tagDupes++;
        }
        Check("CK_Asset_Location_Not_InStorage", ckViolations == 0, $"{assetRows:N0} assets, {ckViolations} violations");
        Check("UNIQUE (ServiceLineId, AssetTag)", tagDupes == 0, $"{tagDupes} duplicates");
        Check("Asset.ParentId resolves", parentIds.All(assetIds.Contains),
            $"{parentIds.Count(p => !assetIds.Contains(p))} dangling");

        // Pass 2: work orders. FK, uniqueness, dense numbering, service line, key format.
        var woIds = new HashSet<Guid>();
        var woKeys = new HashSet<string>(StringComparer.Ordinal);
        var numbersPerLine = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        long woRows = 0, danglingAsset = 0, numDupes = 0, emptySl = 0, sampled = 0, v4 = 0;
        foreach (var w in Read(dir, "WorkOrders"))
        {
            woRows++;
            if (Guid.TryParse(Str(w, "Id"), out var id))
            {
                woIds.Add(id);
                if (sampled < 500)
                {
                    sampled++;
                    var t = id.ToString();
                    if (t[14] == '4' && "89ab".Contains(char.ToLowerInvariant(t[19]))) v4++;
                }
            }
            if (!Guid.TryParse(Str(w, "AssetId"), out var aid) || !assetIds.Contains(aid)) danglingAsset++;

            var sl = Str(w, "ServiceLineId");
            if (sl == Guid.Empty.ToString()) emptySl++;
            if (!woKeys.Add($"{sl}|{Str(w, "Number")}")) numDupes++;

            var numText = Str(w, "Number");
            if (sl is not null && numText is not null && int.TryParse(numText.Split('-')[^1], out var n))
            {
                if (!numbersPerLine.TryGetValue(sl, out var list)) numbersPerLine[sl] = list = new List<int>();
                list.Add(n);
            }
        }
        Check("WorkOrder.AssetId resolves", danglingAsset == 0, $"{woRows:N0} work orders, {danglingAsset} dangling");
        Check("UNIQUE (ServiceLineId, Number)", numDupes == 0, $"{numDupes} duplicates");
        Check("no WorkOrder has an empty ServiceLineId", emptySl == 0, $"{emptySl} empty");
        Check("primary keys are version 4 with RFC variant", v4 == sampled, $"{v4}/{sampled} sampled");

        // Dense 1..N per line. The app's next number is COUNT(*) + 1 over every row in the line, soft
        // deleted included, so a gap means the first work order a load-test user creates collides on the
        // unique index and the whole SaveChanges rolls back.
        var gapLines = 0;
        foreach (var (_, nums) in numbersPerLine)
        {
            nums.Sort();
            if (nums.Count == 0) continue;
            if (nums[0] != 1 || nums[^1] != nums.Count) gapLines++;
        }
        Check("work-order numbers dense 1..N per service line", gapLines == 0,
            $"{numbersPerLine.Count} lines, {gapLines} with gaps");

        // Pass 3: assignments.
        long laborRows = 0, danglingLabor = 0;
        foreach (var l in Read(dir, "WorkOrderLabor"))
        {
            laborRows++;
            if (!Guid.TryParse(Str(l, "WorkOrderId"), out var wid) || !woIds.Contains(wid)) danglingLabor++;
        }
        Check("WorkOrderLabor.WorkOrderId resolves", danglingLabor == 0, $"{laborRows:N0} rows, {danglingLabor} dangling");

        // Pass 3b: access groups. A user pointing at a group that does not exist resolves as unrestricted,
        // so the LocationAccess scan silently never fires and the run measures the easy path.
        var groupIds = new HashSet<Guid>();
        var pinnedGroupIds = new HashSet<Guid>();
        var allPins = new HashSet<Guid>();
        var unresolvablePins = 0;
        var pinnedGroups = 0;
        long malformedPins = 0, emptyPinLists = 0;
        foreach (var g in Read(dir, "AccessGroups"))
        {
            if (Guid.TryParse(Str(g, "Id"), out var gid)) groupIds.Add(gid);
            var pins = Str(g, "LocationIdsJson");
            if (string.IsNullOrWhiteSpace(pins) || pins == "[]") continue;
            pinnedGroups++;
            if (gid != Guid.Empty) pinnedGroupIds.Add(gid);

            // LocationIdsJson has NO foreign key in any tenant migration and nothing validates it at load
            // time, so a pin to an id that was never emitted is invisible. AccessGroupScopeResolver drops
            // unresolvable pins and leaves the group pinned but scoped to nothing, so the members see an
            // empty application and the LocationAccess scan never fires. lnicoara/cmms#2965
            var parsed = ParsePins(pins);
            if (parsed is null) { malformedPins++; continue; }
            // A group that declares itself pinned and yields no pins is scoped to nothing, which is the
            // same deny-all outcome as corrupt JSON and just as invisible.
            if (parsed.Count == 0) { emptyPinLists++; continue; }
            foreach (var pin in parsed)
            {
                allPins.Add(pin);
                if (!locationParent.ContainsKey(pin)) unresolvablePins++;
            }
        }
        Check("every AccessGroup LocationIdsJson parses as an array of location ids", malformedPins == 0,
            $"{malformedPins} malformed");
        Check("no pinned AccessGroup resolves to an empty pin list", emptyPinLists == 0,
            $"{emptyPinLists} pinned but empty");
        Check("at least one access group carries location pins", pinnedGroups > 0,
            $"{groupIds.Count} groups, {pinnedGroups} pinned");
        Check("every AccessGroup location pin resolves to an emitted location", unresolvablePins == 0,
            $"{allPins.Count} pins, {unresolvablePins} unresolvable");

        // A pinned group whose subtree holds no equipment is worse than no pin at all: every check above
        // still passes, and the persona the load test exists to measure queries an empty set. Walk up from
        // each room that actually holds equipment and see whether any ancestor is pinned.
        var reachable = roomsWithEquipment.Count(room =>
        {
            var node = (Guid?)room;
            var hops = 0;
            while (node is { } n && hops++ < 32)
            {
                if (allPins.Contains(n)) return true;
                node = locationParent.TryGetValue(n, out var p) ? p : null;
            }
            return false;
        });
        Check("the pinned subtree contains equipment", allPins.Count == 0 || reachable > 0,
            $"{reachable} of {roomsWithEquipment.Count} equipment-bearing rooms are inside a pinned subtree");

        // Pass 4: members. A null LaborId turns the technician's own work-order query into WHERE 1=0, so
        // the persona that is most of the intended load would measure an empty result set.
        long userRows = 0, nullLabor = 0, danglingGroup = 0, pinnedMembers = 0;
        foreach (var u in Read(dir, "Users"))
        {
            userRows++;
            if (Str(u, "LaborId") is null) nullLabor++;
            var g = Str(u, "DefaultAccessGroupId");
            if (g is not null)
            {
                if (!Guid.TryParse(g, out var gid) || !groupIds.Contains(gid)) danglingGroup++;
                else pinnedMembers++;
            }
        }
        Check("every User has a LaborId", nullLabor == 0, $"{userRows:N0} users, {nullLabor} without");
        Check("User.DefaultAccessGroupId resolves", danglingGroup == 0,
            $"{pinnedMembers} in a group, {danglingGroup} dangling");

        // Provenance. CreatedAtUtc is NOT NULL with no database default, so a missing value fails the
        // insert; a soft-deleted row without DeletedAtUtc is corruption the database accepts silently.
        long noCreated = 0, deletedNoStamp = 0;
        foreach (var w in Read(dir, "WorkOrders"))
        {
            if (Str(w, "CreatedAtUtc") is null) noCreated++;
            if (Str(w, "IsDeleted") is "true" or "True" && Str(w, "DeletedAtUtc") is null) deletedNoStamp++;
        }
        Check("every WorkOrder has CreatedAtUtc", noCreated == 0, $"{noCreated} missing");
        Check("every soft-deleted WorkOrder has DeletedAtUtc", deletedNoStamp == 0, $"{deletedNoStamp} missing");

        // Timeline coherence. An earlier version drew audit timestamps independently of the record's own
        // CreatedAtUtc, so half the edits landed before the record existed. That loads fine and makes every
        // report built on the audit trail nonsense, which is exactly the class of defect a load test would
        // never surface on its own.
        var createdAtById = new Dictionary<Guid, DateTimeOffset>();
        var deletedIds = new HashSet<Guid>();
        var persistedStatus = new Dictionary<Guid, int>();
        var persistedModifiedBy = new Dictionary<Guid, string?>();
        long futureDated = 0;
        var cutoff = ReadGenerationCutoff(dir);
        foreach (var w in Read(dir, "WorkOrders"))
        {
            if (!Guid.TryParse(Str(w, "Id"), out var id)) continue;
            if (DateTimeOffset.TryParse(Str(w, "CreatedAtUtc"), out var c)) createdAtById[id] = c;
            if (Str(w, "IsDeleted") is "true" or "True") deletedIds.Add(id);
            if (Num(w, "Status") is { } st) persistedStatus[id] = (int)st;
            if (Str(w, "ModifiedAtUtc") is not null) persistedModifiedBy[id] = Str(w, "ModifiedBy");
            foreach (var col in new[] { "CreatedAtUtc", "ModifiedAtUtc", "DeletedAtUtc" })
                if (DateTimeOffset.TryParse(Str(w, col), out var t) && cutoff is { } cut && t > cut) futureDated++;
        }
        Check("no WorkOrder timestamp is after the generation date", futureDated == 0, $"{futureDated} future-dated");

        // Pass 5: audit. The column carries an ISJSON check constraint, and a bulk insert would not have
        // told us. Nothing is retained.
        long auditRows = 0, badJson = 0, unknownFields = 0;
        long beforeCreate = 0, deleteRows = 0;
        var sawDeleteFor = new HashSet<Guid>();
        // Replayability: the last status an audit row records must be the status the row actually holds.
        var lastAuditedStatus = new Dictionary<Guid, (DateTimeOffset At, int Status)>();
        var lastAuditActor = new Dictionary<Guid, (DateTimeOffset At, string? Actor)>();
        var timesById = new Dictionary<Guid, List<DateTimeOffset>>();
        foreach (var a in Read(dir, "AuditEvents"))
        {
            auditRows++;
            if (Str(a, "EntityType") == "WorkOrder" && Guid.TryParse(Str(a, "EntityId"), out var eid))
            {
                if (createdAtById.TryGetValue(eid, out var born)
                    && DateTimeOffset.TryParse(Str(a, "OccurredAtUtc"), out var when)
                    && when < born) beforeCreate++;

                if (Num(a, "Action") == 3) { deleteRows++; sawDeleteFor.Add(eid); }

                if (DateTimeOffset.TryParse(Str(a, "OccurredAtUtc"), out var occurred))
                {
                    if (!timesById.TryGetValue(eid, out var ts)) timesById[eid] = ts = new List<DateTimeOffset>();
                    ts.Add(occurred);

                    if (!lastAuditActor.TryGetValue(eid, out var pa) || occurred >= pa.At)
                        lastAuditActor[eid] = (occurred, Str(a, "ActorName"));

                    var cjs = Str(a, "ChangesJson");
                    if (cjs is not null && cjs.Contains("\"Status\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var d2 = JsonDocument.Parse(cjs);
                            if (d2.RootElement.TryGetProperty("Status", out var st)
                                && st.TryGetProperty("new", out var nv) && nv.ValueKind == JsonValueKind.Number
                                && (!lastAuditedStatus.TryGetValue(eid, out var prev) || occurred >= prev.At))
                                lastAuditedStatus[eid] = (occurred, nv.GetInt32());
                        }
                        catch { /* the parse failure is already counted above */ }
                    }
                }
            }

            var cj = Str(a, "ChangesJson");
            if (cj is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(cj);
                // The diff must name properties that actually exist on the entity. A hand-written diff
                // with invented field names parses fine and is still worthless, so parsing is not enough.
                if (Str(a, "EntityType") == "WorkOrder")
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (!WorkOrderColumns.Contains(prop.Name)) { unknownFields++; break; }
            }
            catch { badJson++; }
        }
        Check("every ChangesJson parses (ISJSON)", badJson == 0, $"{auditRows:N0} rows, {badJson} bad");
        Check("audit diffs name real WorkOrder columns", unknownFields == 0, $"{unknownFields} rows with unknown fields");
        Check("no audit row predates the record it describes", beforeCreate == 0, $"{beforeCreate} before create");
        // Replay: the persisted status must be the one the trail last recorded. An artifact where the row
        // says Closed and no audit row ever moved it there cannot be reconciled by any report.
        long unreplayable = 0, actorMismatch = 0, nonIncreasing = 0;
        foreach (var (id, statusText) in persistedStatus)
        {
            if (lastAuditedStatus.TryGetValue(id, out var la) && la.Status != statusText) unreplayable++;
        }
        foreach (var (id, modifiedBy) in persistedModifiedBy)
        {
            if (lastAuditActor.TryGetValue(id, out var la) && la.Actor is not null && la.Actor != modifiedBy)
                actorMismatch++;
        }
        // Compared in EMITTED order, deliberately not sorted first. Sorting would make the check pass on
        // any set of distinct timestamps regardless of the order they were written in, which is a check
        // that cannot fail rather than a check that holds.
        foreach (var (_, ts) in timesById)
        {
            for (var i = 1; i < ts.Count; i++) if (ts[i] <= ts[i - 1]) { nonIncreasing++; break; }
        }
        Check("persisted status matches the last audited status", unreplayable == 0, $"{unreplayable} unreplayable");
        Check("ModifiedBy matches the last audit actor", actorMismatch == 0, $"{actorMismatch} mismatched");
        Check("audit timestamps strictly increase per record", nonIncreasing == 0, $"{nonIncreasing} records collapse");

        Check("every soft-deleted WorkOrder has a Deleted audit row",
            deletedIds.All(sawDeleteFor.Contains),
            $"{deletedIds.Count} deleted, {deleteRows} delete rows, {deletedIds.Count(x => !sawDeleteFor.Contains(x))} missing");

        return new Result(ok, lines);
    }

    /// <summary>
    /// The twelve tables an artifact must carry. Hardcoded ON PURPOSE, unlike the column manifest which is
    /// derived from the EF model: the point of this list is to catch a table the generator FAILED to write,
    /// and a list derived from the same run that produced the gap would agree with the gap.
    /// </summary>
    private static readonly string[] ExpectedTables =
    [
        "AccessGroups", "Accounts", "Assets", "AssetTypes", "AuditEvents", "Companies",
        "Labor", "Models", "ServiceLines", "Users", "WorkOrderLabor", "WorkOrders",
    ];

    /// <summary>
    /// The location ids inside a LocationIdsJson value, or null if the value is not a well-formed array of
    /// Guid strings.
    ///
    /// STRICT, and the first version of this was not, which reintroduced the exact defect the rest of this
    /// file exists to remove. Swallowing a malformed value and returning zero pins made a corrupt group
    /// validate GREEN: it still counted as pinned, contributed no pins, and the reachability check below
    /// passes when there are no pins to reach. Production treats corrupt pin JSON as deny-all, so the
    /// pinned persona would have measured an empty application while every check reported ok.
    /// </summary>
    private static List<Guid>? ParsePins(string json)
    {
        List<Guid> pins = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                if (!Guid.TryParse(e.GetString(), out var g)) return null;
                pins.Add(g);
            }
        }
        catch (JsonException) { return null; }
        return pins;
    }

    private static DateTimeOffset? ReadGenerationCutoff(string dir)
    {
        var f = Path.Combine(dir, "manifest.json");
        if (!File.Exists(f)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(f));
        return doc.RootElement.TryGetProperty("generationDate", out var g)
               && DateOnly.TryParse(g.GetString(), out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero)
            : null;
    }

    private static readonly HashSet<string> WorkOrderColumns = BuildWorkOrderColumns();

    private static HashSet<string> BuildWorkOrderColumns()
    {
        using var bridge = new ModelBridge();
        // The full mapped set, not the insertable subset: the audit diff walks every property, so a create
        // diff names RowVersion even though no artifact row carries that column.
        return bridge.AllPropertyNames<Cmms.Domain.Work.WorkOrder>();
    }

    private static IEnumerable<Dictionary<string, JsonElement>> Read(string dir, string table)
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

    private static string? Str(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind is not JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    private static double? Num(Dictionary<string, JsonElement> row, string key)
        => row.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetDouble() : null;
}
