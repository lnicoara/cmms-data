namespace Cmms.LoadDataGenerator;

/// <summary>
/// Every dial the generator turns. Defaults are the assumed ratios from lnicoara/cmms#2880, which are
/// reasoned from the lifecycle the product enforces plus general maintenance-management behavior. They
/// are NOT measured against a real customer tenant, and the stats report exists so any of them can be
/// checked and overruled rather than quietly trusted.
/// </summary>
public sealed class GeneratorOptions
{
    public string Seed { get; set; } = "cmms-loadtest-v1";
    public string OutDir { get; set; } = "./artifact";
    public int RowsPerChunk { get; set; } = 100_000;

    /// <summary>
    /// Rows written for each table the hand-written emitters do not cover (lnicoara/cmms#2993).
    ///
    /// Modest on purpose. These tables are here so their screens render and their joins execute against a
    /// table with cardinality rather than an empty one; the volume argument in #2880 is about the tables
    /// whose distribution is under test, and those have their own parameters above.
    /// </summary>
    public int SynthesizedRowsPerTable { get; set; } = 25;



    /// <summary>The date the artifact is "as of". Time-dependent derivations key off it, so a run is only
    /// self-consistent against this value, which is why the manifest records it.</summary>
    public DateOnly GenerationDate { get; set; } = new(2026, 8, 4);

    // Volume
    public int WorkOrders { get; set; } = 5_000;
    public int Assets { get; set; } = 2_000;
    public int Users { get; set; } = 20;

    // Reference-data cardinality. The shipped demo seeder spreads 1,000,000 assets over 40 rooms and 30
    // models, which is 25,000 devices per room. Any index whose leading columns select an eighth of the
    // table gets ignored in favor of a scan, so the load test would measure scans where production seeks.
    public int Campuses { get; set; } = 8;
    public int BuildingsPerCampus { get; set; } = 4;
    public int FloorsPerBuilding { get; set; } = 3;
    public int RoomsPerFloor { get; set; } = 8;
    public int Manufacturers { get; set; } = 40;
    public int ModelsPerManufacturer { get; set; } = 12;
    public int AssetTypes { get; set; } = 30;
    public int Accounts { get; set; } = 12;
    public int Crews { get; set; } = 6;

    // Status mix (#2880). A 4M-row table is ~20 years of history for a system running 200k work orders a
    // year, and the open backlog at any instant is thousands, not hundreds of thousands.
    public double ClosedShare { get; set; } = 0.931;
    public double CancelledShare { get; set; } = 0.04;
    public double CompleteShare { get; set; } = 0.025;
    public double PendingAssignmentShare { get; set; } = 0.0015;
    public double AssignedShare { get; set; } = 0.001;
    public double InProgressShare { get; set; } = 0.001;
    public double HoldShare { get; set; } = 0.0005;

    // Origin mix. This is not cosmetic: it IS the null distribution on the four date columns the app
    // bounds queries on, because each write path populates a different column set.
    public double CalendarPmShare { get; set; } = 0.58;
    public double ManualShare { get; set; } = 0.27;
    public double AutoCorrectiveShare { get; set; } = 0.08;
    public double MeterPmShare { get; set; } = 0.07;

    // Soft deletes. No WorkOrder index is filtered on IsDeleted, so these rows sit in all 21 indexes and
    // still consume numbers from the dense per-service-line sequence. Zero is certainly wrong.
    public double WorkOrderDeletedShare { get; set; } = 0.015;
    public double AssetDeletedShare { get; set; } = 0.03;

    // Audit volume. Mean 5.5 updates per work order, so 6.5 audit rows including the create.
    public int MinUpdatesPerWorkOrder { get; set; } = 1;
    public int MaxUpdatesPerWorkOrder { get; set; } = 60;
    public double UpdateSkewPower { get; set; } = 2.6;
    public double StatusFlipShare { get; set; } = 0.65;
    public int MeanUpdatesPerAsset { get; set; } = 2;

    // Service-group split. Every WorkOrder list index is ServiceLineId-led, so a single-group dataset
    // makes all of them degenerate behind their leading key.
    public double ClinicalEngineeringShare { get; set; } = 0.55;

    // Access-group populations. Each combination is a different load test and the run compares them.
    public double UsersWithPinnedGroupShare { get; set; } = 0.40;
    public double UsersWithUnpinnedGroupShare { get; set; } = 0.30;
    public double UsersWithServiceLineRowsShare { get; set; } = 0.70;

    // Children, as a share of work orders.
    public double LaborAssignedShare { get; set; } = 0.85;

    // Correlation ids: populated for interactive saves, null for the nightly scheduler and other
    // off-request writes.
    public double CorrelationIdShare { get; set; } = 0.55;
}
