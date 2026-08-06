using System.Text.Json;
using Cmms.Domain.Assets;
using Cmms.Domain.Auditing;
using Cmms.Domain.Configuration;
using Cmms.Domain.Organization;
using Cmms.Domain.Work;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Produces the artifact. Every row it writes is one the running application could have written, which is
/// the whole point: synthetic data that does not respect how the app is actually used measures a database
/// nobody runs.
/// </summary>
public sealed class Generator
{
    private readonly GeneratorOptions _o;
    private readonly Deterministic _rng;
    private readonly ModelBridge _bridge;
    private readonly ArtifactWriter _out;
    private readonly Stats _stats = new();

    private readonly Guid _ceLine = ServiceLineSeed.ClinicalEngineeringId;
    private readonly Guid _feLine = ServiceLineSeed.FacilitiesId;

    // Spine, held in memory because it is small and every later row references it.
    private readonly List<Guid> _rooms = new();
    private readonly List<Guid> _campuses = new();
    private readonly List<Guid> _models = new();
    private readonly List<Guid> _manufacturers = new();
    private readonly List<Guid> _assetTypes = new();
    private readonly List<Guid> _accounts = new();
    private readonly List<Guid> _laborIds = new();
    private readonly List<(Guid Id, Guid ServiceLineId)> _equipment = new();

    // Per-service-line work order numbering. The app mints numbers as COUNT(*) + 1 against a UNIQUE
    // (ServiceLineId, Number) index, counting soft-deleted rows too, so the generated sequence has to be
    // dense 1..N with no gaps. Leave a gap and the first work order a load-test user creates collides.
    private readonly Dictionary<Guid, int> _woSeq = new();

    // Nothing in the artifact may be dated after the generation date, or the tenant appears to hold
    // records from its own future and every date-bounded query behaves oddly.
    private readonly DateTimeOffset _generationCutoff;

    // #2880: 12% stalled, 45% clean PM, 30% normal corrective, 12% messy, 1% tail.
    private static readonly (int Min, int Max, double Weight)[] UpdateBands =
    {
        (1, 1, 0.12), (3, 4, 0.45), (5, 8, 0.30), (9, 20, 0.12), (21, 60, 0.01),
    };

    public Generator(GeneratorOptions options)
    {
        _o = options;
        _rng = new Deterministic(options.Seed);
        _bridge = new ModelBridge();
        _out = new ArtifactWriter(options.OutDir, options.RowsPerChunk);
        _generationCutoff = new DateTimeOffset(options.GenerationDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
    }

    public Stats Run()
    {
        EmitServiceLines();
        EmitReferenceData();
        EmitLocations();
        EmitLabor();
        EmitAccessGroups();
        EmitUsers();
        EmitAssets();
        EmitWorkOrders();

        _out.Dispose();
        _bridge.Dispose();
        _stats.TableTotals = _out.Totals.ToDictionary(kv => kv.Key, kv => kv.Value.Rows);
        _stats.TableBytes = _out.Totals.ToDictionary(kv => kv.Key, kv => kv.Value.Bytes);
        return _stats;
    }

    // One record's ENTIRE life, emitted in order: created, edited N times, optionally soft-deleted.
    //
    // Order is the point. An earlier version wrote the row first and then invented audit rows at
    // independently drawn timestamps, which produced a dataset where half the edits happened before the
    // record existed, audit rows claimed a status the persisted row contradicted, and soft-deleted rows
    // had delete metadata but no Deleted audit row at all. Every one of those loads without complaint and
    // makes any report built on the audit trail wrong.
    //
    // So the timeline is built first, the entity walks it, and the row is written LAST in its final state.
    private void EmitHistory<T>(
        string table, T entity, string kind, long ordinal,
        DateTimeOffset createdAt, int updateCount, bool deleted,
        IReadOnlyList<string>? narrowProps = null,
        IReadOnlyList<string>? wideProps = null,
        Action<T, int, bool>? applyUpdate = null,
        Action<T>? finalize = null)
        where T : class
    {
        var (actorId, actorName) = Actor(kind, ordinal);

        // A STRICTLY increasing timeline, every point after creation and none past the generation date.
        //
        // Strictness is not pedantry. An earlier version clamped to a midnight cutoff, so any record
        // created on the generation date had its create and all its edits collapse onto one timestamp,
        // and the audit trail lost its order entirely. Evenly spaced steps with jitter bounded to half a
        // step keeps the order intact by construction rather than by luck.
        var events = updateCount + (deleted ? 1 : 0);
        var times = new DateTimeOffset[events];
        if (events > 0)
        {
            var minWindow = TimeSpan.FromSeconds(events + 1);
            if (_generationCutoff - createdAt < minWindow) createdAt = _generationCutoff - minWindow;

            var step = (_generationCutoff - createdAt) / (events + 1);
            for (var t = 0; t < events; t++)
            {
                var jitter = step * (_rng.NextDouble(kind, ordinal, $"jit{t}") * 0.5);
                times[t] = createdAt + step * (t + 1) + jitter;
            }
        }

        // Created provenance, and the create audit row, sharing one timestamp exactly as the interceptor
        // does: it computes one DateTimeOffset per SaveChanges and uses it for both.
        if (entity is Cmms.Domain.Common.IAuditable created)
        {
            created.CreatedAtUtc = createdAt;
            created.CreatedBy = actorName;
        }

        var createEvent = _bridge.CreatedAudit(entity, createdAt, actorId, actorName, CorrelationId(kind, ordinal));
        // AuditEvent.Id defaults to Guid.NewGuid() on construction, so without this the artifact is not
        // reproducible even though every other byte matches.
        createEvent.Id = _rng.Id($"{kind}audit", ordinal, "createid");
        _out.Write("AuditEvents", _bridge.Row(createEvent));
        _stats.AuditCreates++;
        if (table == "WorkOrders") _stats.WorkOrderAuditRows++;

        // Each edit, in order, mutating the entity as it goes so the audit trail and the final row agree.
        var lastActor = actorName;
        for (var u = 0; u < updateCount; u++)
        {
            var key = ordinal * 97 + u;
            var flip = _rng.Chance($"{table}upd", key, "flip", _o.StatusFlipShare);
            var (uActorId, uActorName) = Actor($"{table}upd", key);

            var isLast = u == updateCount - 1;

            // The final edit settles the record. Without this the persisted status is one no audit row
            // ever recorded, so the trail cannot be replayed to the row it belongs to, which is the whole
            // point of keeping an audit trail.
            if (isLast) flip = true;

            var props = flip
                ? narrowProps ?? Array.Empty<string>()
                : (wideProps ?? Array.Empty<string>())
                    .Take(_rng.NextInt($"{table}upd", key, "width", 8, (wideProps?.Count ?? 8) + 1)).ToList();

            // Only a status flip touches the status. Letting every edit move it made StatusFlipShare a
            // fiction, because wide diffs carried a status change too.
            Action<T>? mutate = null;
            if (isLast && finalize is not null) mutate = finalize;
            else if (flip && applyUpdate is not null) mutate = e => applyUpdate(e, u, false);

            var ev = _bridge.UpdatedAudit(
                entity, props, times[u], uActorId, uActorName, CorrelationId($"{table}upd", key), mutate);

            ev.Id = _rng.Id($"{table}audit", key, "updid");
            _out.Write("AuditEvents", _bridge.Row(ev));

            lastActor = uActorName;

            if (flip) _stats.AuditStatusFlips++; else _stats.AuditWideWrites++;
            _stats.AuditUpdates++;
            if (table == "WorkOrders") _stats.WorkOrderAuditRows++;
        }

        // A record with no edits at all still needs its settled state.
        if (updateCount == 0) finalize?.Invoke(entity);

        // The record ends where its last edit left it, stamped by whoever made that edit. Using the
        // CREATE actor here left ModifiedBy disagreeing with the last audit row on most records.
        var lastEdit = updateCount > 0 ? times[updateCount - 1] : (DateTimeOffset?)null;
        if (lastEdit is { } le && entity is Cmms.Domain.Common.IAuditable modified)
        {
            modified.ModifiedAtUtc = le;
            modified.ModifiedBy = lastActor;
        }

        // A soft delete is the last event, and it produces a Deleted audit row of its own. The interceptor
        // rewrites the delete into an update and stamps the modified columns too, so both move.
        if (deleted && entity is Cmms.Domain.Common.ISoftDeletable d)
        {
            var deletedAt = times[^1];
            // The delete is its own event by its own actor, and every column it stamps names that actor.
            // Stamping DeletedBy from one actor while the Deleted audit row names another is a
            // contradiction no application path can produce.
            var (delActorId, delActorName) = Actor($"{kind}del", ordinal);

            d.IsDeleted = true;
            d.DeletedAtUtc = deletedAt;
            d.DeletedBy = delActorName;
            if (entity is Cmms.Domain.Common.IAuditable dm)
            {
                dm.ModifiedAtUtc = deletedAt;
                dm.ModifiedBy = delActorName;
            }

            var del = _bridge.DeletedAudit(entity, deletedAt, delActorId, delActorName, CorrelationId(kind, ordinal));
            del.Id = _rng.Id($"{kind}audit", ordinal, "delid");
            _out.Write("AuditEvents", _bridge.Row(del));
            _stats.AuditDeletes++;
            if (table == "WorkOrders") _stats.WorkOrderAuditRows++;
        }

        // Final state, written last.
        _out.Write(table, _bridge.Row(entity));
    }

    // Reference and spine rows: created once, never edited, never deleted.
    private void Emit<T>(string table, T entity, string kind, long ordinal) where T : class
        => EmitHistory(table, entity, kind, ordinal, AuditTime(kind, ordinal), 0, false);

    private DateTimeOffset AuditTime(string kind, long ordinal)
    {
        var daysBack = _rng.NextInt(kind, ordinal, "auditday", 0, 7300);
        var secs = _rng.NextInt(kind, ordinal, "auditsec", 0, 86400);
        var at = _generationCutoff.AddDays(-daysBack).AddSeconds(secs);
        return at > _generationCutoff ? _generationCutoff : at;
    }

    // 8 percent of rows are attributed to "system", which is the interceptor's fallback when no user is
    // resolved and is what the nightly PM scheduler and the sweeps actually produce.
    private (string?, string) Actor(string kind, long ordinal)
    {
        if (_laborIds.Count == 0 || _rng.Chance(kind, ordinal, "actorsys", 0.08)) return (null, "system");
        var i = _rng.NextInt(kind, ordinal, "actor", 0, Math.Max(1, _o.Users));
        return ($"u-{i}", $"Tech {i:D4}");
    }

    // Null off a request. That is not a gap in the data, it is what the product writes when there is no
    // ambient trace to correlate, which is every scheduler-driven save.
    private string? CorrelationId(string kind, long ordinal)
        => _rng.Chance(kind, ordinal, "corr", _o.CorrelationIdShare)
            ? _rng.Id(kind, ordinal, "corrid").ToString("N")
            : null;

    private void EmitServiceLines()
    {
        Emit("ServiceLines", new ServiceLine
        {
            Id = _ceLine, Name = "Clinical Engineering", Code = "CE", IsActive = true,
        }, "sl", 1);
        Emit("ServiceLines", new ServiceLine
        {
            Id = _feLine, Name = "Facilities", Code = "FE", IsActive = true,
        }, "sl", 2);
    }

    private Guid LineFor(string kind, long ordinal)
        => _rng.Chance(kind, ordinal, "line", _o.ClinicalEngineeringShare) ? _ceLine : _feLine;

    private void EmitReferenceData()
    {
        for (var i = 0; i < _o.Manufacturers; i++)
        {
            var id = _rng.Id("mfg", i);
            _manufacturers.Add(id);
            Emit("Companies", new Company
            {
                Id = id, Name = $"Manufacturer {i:D4}", Code = $"CMP-{i + 1:D4}",
                Types = CompanyType.Manufacturer | CompanyType.Vendor,
                ServiceLineId = LineFor("mfg", i), Enabled = true,
            }, "mfg", i);

            for (var m = 0; m < _o.ModelsPerManufacturer; m++)
            {
                var ord = i * _o.ModelsPerManufacturer + m;
                var mid = _rng.Id("model", ord);
                _models.Add(mid);
                Emit("Models", new Model
                {
                    Id = mid, Name = $"MDL-{ord:D5}", ManufacturerId = id,
                    ServiceLineId = LineFor("mfg", i), Enabled = true,
                }, "model", ord);
            }
        }

        for (var i = 0; i < _o.AssetTypes; i++)
        {
            var id = _rng.Id("atype", i);
            _assetTypes.Add(id);
            Emit("AssetTypes", new AssetType
            {
                Id = id, Name = $"Equipment Type {i:D3}", Kind = AssetKind.Equipment,
                ServiceLineId = LineFor("atype", i), Enabled = true,
            }, "atype", i);
        }

        for (var i = 0; i < _o.Accounts; i++)
        {
            var id = _rng.Id("acct", i);
            _accounts.Add(id);
            Emit("Accounts", new Account
            {
                Id = id, Name = $"Department {i:D3}", Code = $"ACC-{i + 1:D4}",
                ServiceLineId = LineFor("acct", i),
            }, "acct", i);
        }
    }

    // A real tree, not a flat list. Depth and breadth decide whether a pinned access-group subtree is a
    // small slice of the assets or effectively the whole table, and that single property is what makes the
    // LocationAccess scan either fire realistically or not fire at all.
    private void EmitLocations()
    {
        var campusType = _rng.Id("loctype", 1);
        var buildingType = _rng.Id("loctype", 2);
        var floorType = _rng.Id("loctype", 3);
        var roomType = _rng.Id("loctype", 4);
        var names = new[] { ("Campus", campusType), ("Building", buildingType), ("Floor", floorType), ("Room", roomType) };
        for (var i = 0; i < names.Length; i++)
        {
            Emit("AssetTypes", new AssetType
            {
                Id = names[i].Item2, Name = names[i].Item1, Kind = AssetKind.Location,
                ServiceLineId = _feLine, Enabled = true,
            }, "loctype", i + 10);
        }

        long seq = 0;
        for (var c = 0; c < _o.Campuses; c++)
        {
            var campusId = _rng.Id("loc", seq);
            EmitLocation(campusId, null, campusType, $"Campus {c + 1:D2}", seq++);
            _campuses.Add(campusId);

            for (var b = 0; b < _o.BuildingsPerCampus; b++)
            {
                var buildingId = _rng.Id("loc", seq);
                EmitLocation(buildingId, campusId, buildingType, $"Campus {c + 1:D2} Building {b + 1:D2}", seq++);

                for (var f = 0; f < _o.FloorsPerBuilding; f++)
                {
                    var floorId = _rng.Id("loc", seq);
                    EmitLocation(floorId, buildingId, floorType, $"C{c + 1:D2}B{b + 1:D2} Floor {f + 1}", seq++);

                    for (var r = 0; r < _o.RoomsPerFloor; r++)
                    {
                        var roomId = _rng.Id("loc", seq);
                        EmitLocation(roomId, floorId, roomType, $"C{c + 1:D2}B{b + 1:D2}F{f + 1} Room {r + 1:D2}", seq++);
                        _rooms.Add(roomId);
                    }
                }
            }
        }
        _stats.Rooms = _rooms.Count;
    }

    private void EmitLocation(Guid id, Guid? parentId, Guid typeId, string name, long ord)
        => Emit("Assets", new Asset
        {
            Id = id, Kind = AssetKind.Location, AssetTag = $"LOC-{ord + 1:D6}", Name = name,
            ServiceLineId = _feLine, ParentId = parentId, AssetTypeId = typeId,
        }, "loc", ord);

    private void EmitLabor()
    {
        for (var i = 0; i < _o.Users; i++)
        {
            var id = _rng.Id("labor", i);
            _laborIds.Add(id);
            Emit("Labor", new Labor
            {
                Id = id, Name = $"Tech {i:D4}", Code = $"LAB-{i + 1:D4}",
                Email = $"tech{i:D4}@demo-health.invalid",
                ServiceLineId = LineFor("labor", i), IsActive = true,
                AccountId = _accounts.Count > 0 ? _rng.Pick("labor", i, "acct", _accounts) : null,
            }, "labor", i);
        }
    }

    // A member whose LaborId is null makes the "my work orders" query compile to WHERE 1=0, so the
    // technician persona (70 percent of the intended load) would measure an empty result set. Every user
    // here is linked to a Labor row that genuinely appears on assignments.
    // The access groups the users point at. Without these rows the ids on User.DefaultAccessGroupId
    // resolve to nothing, AccessGroupScopeResolver returns null (unrestricted), and the LocationAccess
    // full-equipment scan never fires. That scan is the single loudest predicted failure in the epic, so
    // an artifact that quietly cannot trigger it would make the whole run measure the easy path.
    //
    // Two groups on purpose. The PINNED one carries a non-empty LocationIdsJson and is what makes the
    // scan run; the UNPINNED one has no pins and resolves unrestricted. Both populations have to exist,
    // because the run compares them.
    private Guid _pinnedGroupId;
    private Guid _unpinnedGroupId;

    private void EmitAccessGroups()
    {
        _pinnedGroupId = _rng.Id("group", 1);
        _unpinnedGroupId = _rng.Id("group", 2);

        // Pin a couple of campuses: a subtree small enough that the filter is selective, which is the
        // realistic case. Pinning everything, or nothing, both make the filter meaningless.
        var pinnedCampuses = _campuses.Take(Math.Max(1, _campuses.Count / 4)).ToList();

        Emit("AccessGroups", new Cmms.Domain.Identity.AccessGroup
        {
            Id = _pinnedGroupId,
            Name = "Campus-scoped technicians",
            Description = "Location-pinned: exercises the LocationAccess allow-list path.",
            LocationIdsJson = JsonSerializer.Serialize(pinnedCampuses),
        }, "group", 1);

        Emit("AccessGroups", new Cmms.Domain.Identity.AccessGroup
        {
            Id = _unpinnedGroupId,
            Name = "Unrestricted technicians",
            Description = "Has a group but no location pins, so the scope resolver returns unrestricted.",
            LocationIdsJson = null,
        }, "group", 2);
    }

    private void EmitUsers()
    {
        // One PBKDF2 hash, reused. Verify() reads the iteration count out of the stored string, so a
        // single shared hash is accepted for every member and costs one derivation instead of N.
        const string hash = "pbkdf2.600000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        for (var i = 0; i < _o.Users; i++)
        {
            var pinned = _rng.Chance("user", i, "pinned", _o.UsersWithPinnedGroupShare);
            var unpinned = !pinned && _rng.Chance("user", i, "unpinned", _o.UsersWithUnpinnedGroupShare);

            Emit("Users", new Cmms.Domain.Identity.User
            {
                Id = _rng.Id("user", i),
                Username = $"tech{i:D4}",
                DisplayName = $"Tech {i:D4}",
                Email = $"tech{i:D4}@demo-health.invalid",
                PasswordHash = hash,
                IsActive = true,
                // Leave this true and login returns mustChangePassword with no token, so a harness that
                // pre-mints 500 sessions would get 500 unusable ones.
                MustChangePassword = false,
                LaborId = _laborIds[i],
                DefaultAccessGroupId = pinned ? _pinnedGroupId : unpinned ? _unpinnedGroupId : null,
                DefaultServiceLineId = _rng.Chance("user", i, "slrows", _o.UsersWithServiceLineRowsShare)
                    ? LineFor("user", i) : null,
            }, "user", i);

            if (pinned) _stats.UsersPinnedGroup++;
            else if (unpinned) _stats.UsersUnpinnedGroup++;
            else _stats.UsersNoGroup++;
        }
    }

    private void EmitAssets()
    {
        for (var i = 0; i < _o.Assets; i++)
        {
            var line = LineFor("asset", i);
            var deleted = _rng.Chance("asset", i, "deleted", _o.AssetDeletedShare);

            // Each dimension is drawn independently. The shipped seeder uses modular index arithmetic
            // (rooms[i % 40], types[i % 8]) where the divisors share factors, so the room fully determines
            // the type, manufacturer and account. That is worse than uniform for query-plan realism,
            // because a multi-column predicate then sees selectivity no real tenant produces.
            var mfgIdx = _rng.NextInt("asset", i, "mfg", 0, _manufacturers.Count);
            var state = _rng.Pick("asset", i, "state", new[]
            {
                (EquipmentState.Active, 0.86), (EquipmentState.OutOfService, 0.05),
                (EquipmentState.InStorage, 0.04), (EquipmentState.Retired, 0.04),
                (EquipmentState.Quarantined, 0.01),
            });

            var id = _rng.Id("asset", i);
            _equipment.Add((id, line));

            var assetUpdates = _rng.Skewed("asset", i, "upd", 0, _o.MeanUpdatesPerAsset * 4, 2.0);
            var asset = new Asset
            {
                Id = id,
                Kind = AssetKind.Equipment,
                AssetTag = $"EQ-{i + 1:D6}",
                Name = $"Device {i + 1:D6}",
                ServiceLineId = line,
                ParentId = _rng.Pick("asset", i, "room", _rooms),
                AssetTypeId = _rng.Pick("asset", i, "type", _assetTypes),
                ManufacturerId = _manufacturers[mfgIdx],
                ModelId = _rng.Pick("asset", i, "model", _models),
                AccountId = _rng.Pick("asset", i, "acct", _accounts),
                Serial = $"SN{_rng.Next("asset", i, "serial") % 1_000_000_000:D9}",
                InstallationDate = new DateOnly(2010, 1, 1).AddDays(_rng.NextInt("asset", i, "install", 0, 5800)),
                State = state,
                // FreeStatusCode is normally derived at save time by the context, not by feature code, so
                // the generator has to run the same mapping or the column disagrees with State.
                FreeStatusCode = AssetStatusDefaults.FreeCodeForState(state),
            };

            EmitHistory("Assets", asset, "asset", i,
                AuditTime("asset", i), assetUpdates, deleted, AssetNarrow, AssetWide,
                (a, u, _) => a.State = u % 2 == 0 ? EquipmentState.OutOfService : EquipmentState.Active,
                a => { a.State = state; a.FreeStatusCode = AssetStatusDefaults.FreeCodeForState(state); });

            if (deleted) _stats.AssetsDeleted++;
            _stats.AssetStates[state] = _stats.AssetStates.GetValueOrDefault(state) + 1;
        }
    }

    private void EmitWorkOrders()
    {
        for (var i = 0; i < _o.WorkOrders; i++)
        {
            var (assetId, line) = _equipment[_rng.NextInt("wo", i, "asset", 0, _equipment.Count)];

            var status = _rng.Pick("wo", i, "status", new[]
            {
                (WorkOrderStatus.Closed, _o.ClosedShare),
                (WorkOrderStatus.Cancelled, _o.CancelledShare),
                (WorkOrderStatus.Complete, _o.CompleteShare),
                (WorkOrderStatus.PendingAssignment, _o.PendingAssignmentShare),
                (WorkOrderStatus.Assigned, _o.AssignedShare),
                (WorkOrderStatus.InProgress, _o.InProgressShare),
                (WorkOrderStatus.Hold, _o.HoldShare),
            });

            var origin = _rng.Pick("wo", i, "origin", new[]
            {
                ("calendarpm", _o.CalendarPmShare), ("manual", _o.ManualShare),
                ("autocorrective", _o.AutoCorrectiveShare), ("meterpm", _o.MeterPmShare),
            });

            // Dates weighted toward recent: the hot slice every default view bounds on is the last 90 days,
            // and a uniform 20-year spread would make that slice 1 percent of the table instead of the
            // share a real tenant queries.
            var ageDays = _rng.Skewed("wo", i, "age", 0, 7300, 2.2);
            var created = _o.GenerationDate.AddDays(-ageDays);

            DateOnly? due = origin is "calendarpm"
                ? created.AddDays(_rng.NextInt("wo", i, "due", 7, 120))
                : origin is "manual" ? created.AddDays(_rng.NextInt("wo", i, "due", 1, 45)) : null;

            // CompletedDate is drawn independently rather than as created + constant. Deriving it would
            // make the two distributions identical and hide that CompletedDate has no index while
            // (ServiceLineId, CreatedDate) does, so the missing one would look as good as the present one.
            DateOnly? completed = status is WorkOrderStatus.Complete or WorkOrderStatus.Closed
                ? created.AddDays(_rng.Skewed("wo", i, "lag", 0, 400, 3.2))
                : null;
            if (completed > _o.GenerationDate) completed = _o.GenerationDate;

            var deleted = _rng.Chance("wo", i, "deleted", _o.WorkOrderDeletedShare);

            // Dense per-line numbering, soft-deleted rows included, because the app's next mint is
            // COUNT(*) + 1 over every row in the line.
            _woSeq.TryGetValue(line, out var n);
            _woSeq[line] = ++n;

            var id = _rng.Id("wo", i);
            var isPm = origin is "calendarpm" or "meterpm";

            // The #2880 bands, verbatim. Mean lands near 5.5, which with the create gives ~6.5 audit rows
            // per work order. The 1 percent tail out to 60 is deliberate: ActivityEndpoints reads every
            // audit row for a record with no paging or limit, so the tail is exactly where that endpoint
            // breaks, and a distribution without it would never exercise the failure.
            var updates = _rng.Band("wo", i, "upd", UpdateBands);

            var workOrder = new WorkOrder
            {
                Id = id,
                ServiceLineId = line,
                Number = $"WO-{n:D5}",
                AssetId = assetId,
                Status = status,
                WorkSummary = origin switch
                {
                    "calendarpm" => "Scheduled preventive maintenance",
                    "meterpm" => "Meter-based preventive maintenance",
                    "autocorrective" => "Corrective follow-up from failed step",
                    _ => "Service request",
                },
                Description = origin is "manual" ? "Reported by department staff." : null,
                LocationNotes = origin is "manual" or "autocorrective" ? "See asset location." : null,
                CreatedDate = created,
                RequestedDate = origin is "manual" ? created : null,
                ScheduledDate = null, // never set on a PM-origin row, per the generation paths
                DueDate = due,
                CompletedDate = completed,
                ClosedDate = status is WorkOrderStatus.Closed ? completed : null,
                ComplianceStatus = Compliance.Evaluate(status, completed, due, _o.GenerationDate),
                InitiationMethod = isPm || origin is "autocorrective"
                    ? WorkOrderInitiationMethod.BySystem
                    : WorkOrderInitiationMethod.ByUser,
                PmScheduleId = null,
            };

            // Born PendingAssignment, like every work order the application creates. The flips walk it and
            // the finalize step settles it on the status this record is meant to end in.
            var finalStatus = workOrder.Status;
            workOrder.Status = WorkOrderStatus.PendingAssignment;

            EmitHistory("WorkOrders", workOrder, "wo", i,
                new DateTimeOffset(created.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                updates, deleted, WorkOrderNarrow, WorkOrderWide,
                (w, u, _) => w.Status = u % 2 == 0 ? WorkOrderStatus.InProgress : WorkOrderStatus.Assigned,
                w => w.Status = finalStatus);

            _stats.Statuses[status] = _stats.Statuses.GetValueOrDefault(status) + 1;
            _stats.Origins[origin] = _stats.Origins.GetValueOrDefault(origin) + 1;
            if (deleted) _stats.WorkOrdersDeleted++;
            if (isPm) _stats.PmWorkOrders++;
            if (due is null) _stats.NullDueDate++;
            if (completed is null) _stats.NullCompletedDate++;

            if (_rng.Chance("wo", i, "labor", _o.LaborAssignedShare) && _laborIds.Count > 0)
            {
                var laborId = _rng.Pick("wo", i, "laborid", _laborIds);
                Emit("WorkOrderLabor", new WorkOrderLabor
                {
                    WorkOrderId = id, LaborId = laborId,
                    AssignmentDate = created,
                    EstimatedHours = 1m + _rng.NextInt("wo", i, "est", 0, 6),
                }, "wol", i);
                _stats.LaborAssignments++;
            }

            _stats.UpdateHistogram[Math.Min(updates, 60)] = _stats.UpdateHistogram.GetValueOrDefault(Math.Min(updates, 60)) + 1;
        }
    }

    // Real column names off the entities, so a diff names fields that actually exist.
    private static readonly string[] WorkOrderNarrow = { nameof(WorkOrder.Status) };

    private static readonly string[] WorkOrderWide =
    {
        nameof(WorkOrder.WorkSummary), nameof(WorkOrder.Description), nameof(WorkOrder.LocationNotes),
        nameof(WorkOrder.PriorityId), nameof(WorkOrder.DueDate), nameof(WorkOrder.ScheduledDate),
        nameof(WorkOrder.AccountId), nameof(WorkOrder.WorkTypeId), nameof(WorkOrder.SubTypeId),
        nameof(WorkOrder.ProblemCodeId), nameof(WorkOrder.CauseCodeId), nameof(WorkOrder.ResolutionCodeId),
        nameof(WorkOrder.RequestedDate), nameof(WorkOrder.CrewId), nameof(WorkOrder.ProjectId),
        nameof(WorkOrder.CustomerId), nameof(WorkOrder.CompletedDate), nameof(WorkOrder.ClosedDate),
        nameof(WorkOrder.ComplianceStatus), nameof(WorkOrder.SubStatusId),
    };

    private static readonly string[] AssetNarrow = { nameof(Asset.State), nameof(Asset.FreeStatusCode) };

    private static readonly string[] AssetWide =
    {
        nameof(Asset.Name), nameof(Asset.Serial), nameof(Asset.ParentId), nameof(Asset.AccountId),
        nameof(Asset.ModelId), nameof(Asset.ManufacturerId), nameof(Asset.AssetTypeId),
        nameof(Asset.InstallationDate), nameof(Asset.AssetTag), nameof(Asset.State),
    };
}
