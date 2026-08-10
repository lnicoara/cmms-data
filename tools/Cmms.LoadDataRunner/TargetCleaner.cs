using Cmms.LoadDataGenerator;
using Microsoft.Data.SqlClient;

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
    /// <summary>SQL Server error 547: the statement conflicted with a REFERENCE constraint.</summary>
    private const int ForeignKeyConflict = 547;

    public sealed record CleanReport(
        bool Executed, IReadOnlyList<(string Table, long Rows)> Cleared, List<string> Lines,
        IReadOnlyList<Dictionary<string, object?>> SavedUsers,
        IReadOnlyList<Dictionary<string, object?>> SavedAccessGroups);

    /// <summary>
    /// Logins the clear holds aside. 'admin' is the name the provisioner and the seed runner both use for
    /// the account they create, so it is the one an operator will actually have.
    /// </summary>
    public static readonly string[] PreservedUsernames = { "admin" };

    /// <summary>
    /// Puts the held-aside login back, after the load. Returns how many user rows were written.
    ///
    /// Access groups first, because the user row points at one. Lives here rather than inline in Program
    /// for the same reason ClearAsync does: Program is top-level statements, so anything written there can
    /// only be tested by running the whole binary, and the step that decides whether anyone can log in
    /// afterwards is the last one that should be untestable.
    /// </summary>
    public static async Task<int> RestorePreservedAsync(
        TargetDatabase target, CleanReport clean, CancellationToken ct)
    {
        if (!clean.Executed || clean.SavedUsers.Count == 0) return 0;

        await target.RestoreRowsAsync("AccessGroups", clean.SavedAccessGroups, ct);
        var users = await target.RestoreRowsAsync("Users", clean.SavedUsers, ct);

        // Asserted, not assumed. A load that finishes with nobody able to sign in is the exact failure this
        // path exists to prevent, and it went unnoticed precisely because the run reported success.
        if (users != clean.SavedUsers.Count)
            throw new InvalidOperationException(
                $"expected to restore {clean.SavedUsers.Count} login(s) but wrote {users}. This tenant may " +
                "have no working account; check [Users] before treating the load as done.");

        return users;
    }


    /// <summary>
    /// Deletes every row from each table the artifact owns, children before parents.
    ///
    /// Reverse dependency order matters and is not cosmetic here the way load order is. Load order is
    /// defensive because SqlBulkCopy does not check constraints; DELETE always does, so deleting a parent
    /// before its children fails outright on the foreign key.
    /// </summary>
    /// <param name="onTable">
    /// Called with each table's name just before it is emptied, so the caller can say which one a failure
    /// happened on. The clear is one statement per table and the slow one is invisible otherwise: a run
    /// that stopped on AuditEvents reported only that something timed out, and the 134 tables it had
    /// already emptied in the preceding second made it look like the clear had barely started.
    /// </param>
    /// <summary>
    /// Clears the tables an ARTIFACT owns, plus their foreign-key closure. The load path's entry point.
    /// </summary>
    public static Task<CleanReport> ClearAsync(
        LoadPlan plan, ModelBridge model, TargetDatabase target, bool execute, CancellationToken ct,
        Action<string>? onTable = null)
        => ClearAsync(plan.Tables.Select(t => t.Table).ToList(), model, target, execute, ct, onTable);

    /// <summary>
    /// Clears a named set of tables, plus their foreign-key closure. lnicoara/cmms#3055.
    ///
    /// Separate from the artifact-shaped overload because emptying a tenant is not a question about a
    /// dataset. Passing every table in the model asks for the tenant to be emptied; passing an artifact's
    /// tables asks for room to be made for that artifact. Both are legitimate and they are not the same
    /// request, so a caller has to say which one it means.
    /// </summary>
    public static async Task<CleanReport> ClearAsync(
        IReadOnlyList<string> tables, ModelBridge model, TargetDatabase target, bool execute,
        CancellationToken ct, Action<string>? onTable = null)
    {
        var lines = new List<string>();
        var cleared = new List<(string, long)>();

        // Not just the tables named. Clearing has to cover everything that REFERENCES them, or the DELETE
        // fails on a foreign key from a table the caller never mentioned. See LoadPlan.ClearOrder.
        var order = LoadPlan.ClearOrder(tables, model);
        var extra = order.Count - tables.Count;

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
                        order.Where(t => !tables.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase)))));
            return new CleanReport(false, cleared, lines, Array.Empty<Dictionary<string, object?>>(),
                Array.Empty<Dictionary<string, object?>>());
        }

        // Who must still be able to log in when this finishes.
        //
        // The clear used to empty Users like any other table, and the artifact then refilled it with
        // synthetic accounts whose password hash is a placeholder that matches no password. The guaranteed
        // end state was a tenant nobody could sign into, reported as a success, and recovering from it took
        // a platform-admin token and a hand-written API call. A load-test dataset that locks you out of the
        // system being load-tested is not a working dataset.
        //
        // Preserving beats re-creating: nothing new is minted, no password has to be supplied on the
        // command line or embedded in a generated artifact, and the account that worked before the load is
        // the account that works after it.
        var preserved = await target.FindUsersByNameAsync(PreservedUsernames, ct);
        var userIds = preserved.Select(p => p.UserId).ToList();
        var groupIds = preserved.Where(p => p.AccessGroupId is not null)
                                .Select(p => p.AccessGroupId!.Value).Distinct().ToList();

        // Captured now, restored by the caller after the load. The account's own row travels intact, so the
        // password that worked before the load is the password that works after it, and nothing new has to
        // be minted or typed on a command line.
        var savedUsers = await target.CaptureRowsAsync("Users", userIds, ct);
        var savedGroups = await target.CaptureRowsAsync("AccessGroups", groupIds, ct);

        Say($"CLEARING {order.Count} tables in {target.DatabaseName}. This is destructive.");
        if (savedUsers.Count > 0)
            Say($"  holding {savedUsers.Count} login(s) [{string.Join(", ", PreservedUsernames)}] and " +
                $"{savedGroups.Count} access group(s) aside; they are restored after the load");
        else
            Say($"  NOTE: no account named {string.Join("/", PreservedUsernames)} exists here, so this " +
                "clear leaves no login behind. The artifact's users cannot authenticate.");

        // Deleted to a FIXPOINT rather than in one ordered pass.
        //
        // Reverse dependency order is correct for REQUIRED foreign keys, which are acyclic. Optional ones
        // are not: ServiceCodes.ServiceLineId is nullable, so the load order relaxes that edge and places
        // ServiceCodes before ServiceLines, and the reverse walk then deletes the parent while children
        // still point at it. Covering 12 tables hid this; covering 158 made it ordinary.
        //
        // Nulling the optional columns first was tried and is wrong: SQL Server treats NULLs as EQUAL in a
        // unique index, so blanking ServiceLineId across every row collides on
        // IX_ServiceCodes_OneEnabledPerLine. The column cannot be emptied, so the ORDER has to give way.
        //
        // So each pass deletes whatever it can and defers what a foreign key refuses, and passes repeat
        // while any progress is made. Every pass strips the current leaves of the reference graph, so a
        // graph of N tables converges in at most N passes whatever its shape, and no row is ever modified
        // on the way out. A pass that deletes nothing while rows remain is a genuine required-key cycle,
        // which is a schema fact worth failing on. lnicoara/cmms#2993.
        var pending = Enumerable.Reverse(order).ToList();
        for (var pass = 1; pending.Count > 0; pass++)
        {
            var deferred = new List<string>();
            var progressed = false;

            foreach (var table in pending)
            {
                onTable?.Invoke(table);
                try
                {
                    var deleted = await target.ClearTableAsync(table, ct, Say);
                    cleared.Add((table, deleted));
                    if (deleted > 0) Say($"  {table,-24} {deleted,12:N0} rows deleted");
                    progressed = true;
                }
                catch (SqlException ex) when (ex.Number == ForeignKeyConflict)
                {
                    // A child still holds a reference. It is ahead of us in this pass, so try again next.
                    deferred.Add(table);
                }
            }

            if (deferred.Count > 0 && !progressed)
                throw new InvalidOperationException(
                    $"clear stalled on pass {pass}: {deferred.Count} tables each blocked by a foreign key " +
                    "from another blocked table, so no order empties them. Required keys form a cycle: " +
                    string.Join(", ", deferred.OrderBy(t => t, StringComparer.Ordinal)));

            pending = deferred;
        }

        // Verify rather than assume. The clear is a sequence of independent DELETEs with no encompassing
        // transaction, deliberately: wrapping 42.7 million rows in one would blow the log on a General
        // Purpose tier, and a DELETE-everything is idempotent so a partial clear is fixed by running it
        // again. What that trade needs is proof it finished, or a partial clear would hand the loader a
        // target it then refuses for reasons that look unrelated.
        var stillPopulated = new List<string>();
        foreach (var table in order)
        {
            if (await target.HasAnyRowAsync(table, ct))
                stillPopulated.Add(table);
        }

        if (stillPopulated.Count > 0)
            throw new InvalidOperationException(
                "Clear did not empty " + string.Join(", ", stillPopulated) +
                ". Rows were written concurrently, or a foreign key from outside the artifact's write set " +
                "is holding them. Resolve it and re-run; clearing is idempotent.");

        lines.Add("cleared, and every target table verified empty");
        return new CleanReport(true, cleared, lines, savedUsers, savedGroups);
    }
}
