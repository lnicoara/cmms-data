using Cmms.Domain.Assets;
using Cmms.Domain.Work;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// What the run actually produced, reported next to what it was asked to produce.
///
/// This is what makes the fidelity claim checkable BEFORE anyone spends hours loading tens of gigabytes.
/// Without it, "the data is realistic" is an assertion; with it, it is an observation anyone can read off
/// a file and disagree with.
/// </summary>
public sealed class Stats
{
    public long AuditCreates;
    public long WorkOrderAuditRows;
    public long AuditUpdates;
    public long AuditDeletes;
    public long AuditStatusFlips;
    public long AuditWideWrites;
    public long WorkOrdersDeleted;
    public long AssetsDeleted;
    public long PmWorkOrders;
    public long LaborAssignments;
    public long NullDueDate;
    public long NullCompletedDate;
    public int Rooms;
    public int UsersPinnedGroup;
    public int UsersUnpinnedGroup;
    public int UsersNoGroup;

    public Dictionary<WorkOrderStatus, long> Statuses = new();
    public Dictionary<string, long> Origins = new();
    public Dictionary<EquipmentState, long> AssetStates = new();
    public Dictionary<int, long> UpdateHistogram = new();
    public Dictionary<string, long> TableTotals = new();
    public Dictionary<string, long> TableBytes = new();

    public object ToReport(GeneratorOptions o)
    {
        var wo = Statuses.Values.Sum();
        var assets = AssetStates.Values.Sum();
        double Share(long n, long d) => d == 0 ? 0 : Math.Round((double)n / d, 5);

        long Open() =>
            Statuses.GetValueOrDefault(WorkOrderStatus.PendingAssignment)
            + Statuses.GetValueOrDefault(WorkOrderStatus.Assigned)
            + Statuses.GetValueOrDefault(WorkOrderStatus.InProgress)
            + Statuses.GetValueOrDefault(WorkOrderStatus.Hold);

        var auditTotal = AuditCreates + AuditUpdates + AuditDeletes;

        return new
        {
            observedVsTarget = new object[]
            {
                Row("status.closed", Share(Statuses.GetValueOrDefault(WorkOrderStatus.Closed), wo), o.ClosedShare),
                Row("status.cancelled", Share(Statuses.GetValueOrDefault(WorkOrderStatus.Cancelled), wo), o.CancelledShare),
                Row("status.complete", Share(Statuses.GetValueOrDefault(WorkOrderStatus.Complete), wo), o.CompleteShare),
                Row("origin.calendarPm", Share(Origins.GetValueOrDefault("calendarpm"), wo), o.CalendarPmShare),
                Row("origin.manual", Share(Origins.GetValueOrDefault("manual"), wo), o.ManualShare),
                Row("origin.autoCorrective", Share(Origins.GetValueOrDefault("autocorrective"), wo), o.AutoCorrectiveShare),
                Row("origin.meterPm", Share(Origins.GetValueOrDefault("meterpm"), wo), o.MeterPmShare),
                Row("workOrder.isDeleted", Share(WorkOrdersDeleted, wo), o.WorkOrderDeletedShare),
                Row("asset.isDeleted", Share(AssetsDeleted, assets), o.AssetDeletedShare),
                // Runs ABOVE its target on purpose, and the gap is not drift. The last edit of every record is
                // forced to be a status flip so the settled status is an audited event and the trail can be
                // replayed onto the row. That adds one guaranteed flip per record on top of the sampled ones.
                Row("audit.statusFlipShare", Share(AuditStatusFlips, AuditUpdates), o.StatusFlipShare),
                Row("workOrder.laborAssigned", Share(LaborAssignments, wo), o.LaborAssignedShare),
            },
            derived = new
            {
                // #2880 predicts ~0.4 percent open and ~6.5 audit rows per work order. If these land far
                // off, the declared shares and the skew are inconsistent with each other.
                openShare = Share(Open(), wo),
                pmShare = Share(PmWorkOrders, wo),
                auditRowsPerWorkOrder = wo == 0 ? 0 : Math.Round((double)WorkOrderAuditRows / wo, 2),
                auditRowsAllEntities = auditTotal,
                nullDueDateShare = Share(NullDueDate, wo),
                nullCompletedDateShare = Share(NullCompletedDate, wo),
            },
            cardinality = new
            {
                rooms = Rooms,
                assetsPerRoom = Rooms == 0 ? 0 : Math.Round((double)assets / Rooms, 1),
                assetsPerModel = Math.Round((double)assets / Math.Max(1, o.Manufacturers * o.ModelsPerManufacturer), 1),
            },
            users = new { pinnedGroup = UsersPinnedGroup, unpinnedGroup = UsersUnpinnedGroup, noGroup = UsersNoGroup },
            audit = new
            {
                creates = AuditCreates, updates = AuditUpdates, deletes = AuditDeletes, total = auditTotal,
                statusFlips = AuditStatusFlips, wideWrites = AuditWideWrites,
            },
            updateHistogram = UpdateHistogram.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(), k => k.Value),
            tables = TableTotals.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
            uncompressedBytes = TableBytes.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
        };
    }

    private static object Row(string name, double observed, double target) => new
    {
        parameter = name,
        observed,
        target,
        deltaPct = target == 0 ? 0 : Math.Round((observed - target) / target * 100, 1),
    };
}
