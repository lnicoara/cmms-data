using Cmms.LoadDataGenerator;

namespace Cmms.LoadDataRunner;

/// <summary>
/// Empties the artifact's write set. Destructive, separate from the loader, and deliberately its own type.
///
/// lnicoara/cmms#2908 grill round 3 settled that the loader never deletes, and that still holds: nothing on
/// the load path can reach this. It exists because the chosen pre-prod target is the already-seeded
/// demo-health tenant, whose ServiceLines rows carry the same well-known Guids the artifact ships
/// (ServiceLineSeed), so those two rows collide on the primary key and a load cannot start until they go.
///
/// Living in its own class rather than inline in Program is what makes it testable at all: Program is
/// top-level statements, so a clear written there could only ever be verified by running the whole binary.
/// A destructive operation is the last thing that should be the untested part of a tool.
/// </summary>
public static class TargetCleaner
{
    public sealed record CleanReport(bool Executed, IReadOnlyList<(string Table, long Rows)> Cleared, List<string> Lines);

    /// <summary>
    /// Deletes every row from each table the artifact owns, children before parents.
    ///
    /// Reverse dependency order matters and is not cosmetic here the way load order is. Load order is
    /// defensive because SqlBulkCopy does not check constraints; DELETE always does, so deleting a parent
    /// before its children fails outright on the foreign key.
    /// </summary>
    public static async Task<CleanReport> ClearAsync(
        LoadPlan plan, ModelBridge model, TargetDatabase target, bool execute, CancellationToken ct)
    {
        var lines = new List<string>();
        var cleared = new List<(string, long)>();

        // Not plan.Tables. Clearing has to cover everything that REFERENCES the artifact's tables, or the
        // DELETE fails on a foreign key from a table the plan has never heard of. See LoadPlan.ClearOrder.
        var order = LoadPlan.ClearOrder(plan.Tables.Select(t => t.Table).ToList(), model);
        var extra = order.Count - plan.Tables.Count;

        // Written to the console AS IT GOES, not only collected for the caller to print afterwards. These
        // DELETEs commit independently, so a throw partway through leaves real rows gone; buffering the
        // record of what went meant the one run that failed reported which constraint stopped it and NOTHING
        // about the eight tables it had already emptied.
        void Say(string line) { lines.Add(line); Console.WriteLine(line); }

        if (!execute)
        {
            Say($"PLAN: would delete every row from {order.Count} tables in {target.DatabaseName}, " +
                "in reverse dependency order. Nothing deleted (no --execute).");
            if (extra > 0)
                Say($"  {extra} of those are NOT in the artifact; they are emptied because they reference " +
                    "tables that are: " + string.Join(", ",
                        order.Where(t => !plan.Tables.Any(p => string.Equals(p.Table, t, StringComparison.OrdinalIgnoreCase)))));
            return new CleanReport(false, cleared, lines);
        }

        Say($"CLEARING {order.Count} tables in {target.DatabaseName}. This is destructive.");
        foreach (var table in Enumerable.Reverse(order))
        {
            var deleted = await target.ClearTableAsync(table, ct);
            cleared.Add((table, deleted));
            Say($"  {table,-24} {deleted,12:N0} rows deleted");
        }
        // Verify rather than assume. The clear is a sequence of independent DELETEs with no encompassing
        // transaction, deliberately: wrapping 42.7 million rows in one would blow the log on a General
        // Purpose tier, and a DELETE-everything is idempotent so a partial clear is fixed by running it
        // again. What that trade needs is proof it finished, or a partial clear would hand the loader a
        // target it then refuses for reasons that look unrelated.
        var stillPopulated = new List<string>();
        foreach (var table in order)
            if (await target.HasAnyRowAsync(table, ct))
                stillPopulated.Add(table);

        if (stillPopulated.Count > 0)
            throw new InvalidOperationException(
                "Clear did not empty " + string.Join(", ", stillPopulated) +
                ". Rows were written concurrently, or a foreign key from outside the artifact's write set " +
                "is holding them. Resolve it and re-run; clearing is idempotent.");

        lines.Add("cleared, and every target table verified empty");
        return new CleanReport(true, cleared, lines);
    }
}
