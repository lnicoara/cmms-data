namespace Cmms.LoadDataRunner;

/// <summary>
/// What one load run is pointed at, and whether it is allowed to write.
///
/// <see cref="Execute"/> defaults to false and that default is the safety property, not a convenience.
/// A run with no <c>--execute</c> preflights everything and prints the plan without inserting a row, so
/// the way to discover a miswired target is to look at the plan rather than to look at the damage.
/// </summary>
public sealed class LoaderOptions
{
    /// <summary>Directory holding manifest.json and one subdirectory of chunk files per table.</summary>
    public string Artifact { get; set; } = "";

    /// <summary>Tenant slug. Resolved through the catalog and the tenant's own connection secret.</summary>
    public string Tenant { get; set; } = "";

    /// <summary>False plans, true writes. Nothing else changes between the two.</summary>
    public bool Execute { get; set; }

    /// <summary>
    /// Rows per SqlBulkCopy batch INSIDE one chunk transaction. This is a memory and network knob only:
    /// the transaction boundary is the chunk (grill round 2), so batch size cannot affect atomicity.
    /// </summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>Seconds. A 100,000-row chunk over a WAN into a cold serverless database needs headroom.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Where checkpoint blobs live. Overridable so a test can point at a directory instead of Azure;
    /// in the job this is the configured blob store.
    /// </summary>
    public string CheckpointContainer { get; set; } = "load-checkpoints";

    /// <summary>
    /// The slug of the tenant whose write set may be emptied. DESTRUCTIVE, and deliberately awkward: it
    /// needs this AND <see cref="Execute"/>, so neither alone deletes anything.
    ///
    /// It carries the SLUG rather than a boolean, and that is the whole point. A boolean here would mean
    /// the only thing standing between a wrong <see cref="Tenant"/> and an emptied tenant is a wrapper
    /// script, and a Bicep deploy, a portal edit, or a job env change all bypass wrapper scripts. Naming
    /// the tenant makes the confirmation travel with the operation to the place that performs it, so the
    /// two values have to agree at the point of deletion rather than somewhere upstream of it.
    ///
    /// It exists because the pre-prod target is the already-seeded demo-health tenant, whose ServiceLines
    /// rows carry the same well-known Guids the artifact ships (ServiceLineSeed), so a load would collide
    /// on the primary key. Clearing those tables is what makes a seeded tenant a valid target.
    /// </summary>
    public string ClearTargetSlug { get; set; } = "";

    /// <summary>
    /// Empty the tenant and STOP, loading nothing. lnicoara/cmms#3055.
    ///
    /// Clearing already existed but only as a prelude to a load, so emptying pre-prod meant loading a
    /// dataset you did not want in order to reach the delete that ran first. This makes the delete the
    /// whole job.
    ///
    /// It does NOT loosen the fence. <see cref="ClearTargetSlug"/> and <see cref="Execute"/> are both still
    /// required, and the slug must still match the resolved tenant, so this adds a way to stop early rather
    /// than a way to delete more easily.
    ///
    /// No artifact is needed or read. The table set comes from the EF model, which is the right source for
    /// "empty this tenant": an artifact's tables are whatever that dataset happened to carry, and clearing
    /// to that shape would leave rows behind for a reason nobody could see.
    /// </summary>
    public bool ClearOnly { get; set; }

    /// <summary>
    /// Count every table in the tenant database and STOP, loading nothing and deleting nothing.
    ///
    /// It needs no artifact, and requiring one would defeat the point: "what is in this tenant" is a
    /// question about the DATABASE, not about a dataset, and answering it would otherwise mean staging
    /// gigabytes of an artifact nobody wants in order to reach a SELECT. Same reasoning that gave
    /// <see cref="ClearOnly"/> its own path (lnicoara/cmms#3055).
    ///
    /// READ-ONLY, and that is why it carries no fence. <see cref="ClearOnly"/> needs a matching slug and
    /// <see cref="Execute"/> because it destroys rows; this one cannot change anything, so guarding it
    /// would be ceremony that teaches an operator to type confirmations without reading them.
    ///
    /// The table set is every user table in the database rather than the EF model's or an artifact's. A
    /// count exists to show what is THERE, so deriving the list from either would hide exactly the rows
    /// that make the answer surprising: a table the model has since dropped, or one an earlier dataset
    /// left behind.
    /// </summary>
    public bool CountOnly { get; set; }

    /// <summary>
    /// Validate the ARTIFACT and exit, touching no database at all.
    ///
    /// This exists because artifact verification and target preflight are different questions that were
    /// wrongly fused. The container stages 3.7 GB and then wants to know the download is intact BEFORE it
    /// empties a tenant. Asking that with a normal plan run does not work: a plan also preflights the
    /// target, and a seeded target legitimately fails preflight, which killed the run before the clear
    /// that was about to fix it.
    /// </summary>
    public bool VerifyArtifactOnly { get; set; }
}
