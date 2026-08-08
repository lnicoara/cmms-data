namespace Cmms.LoadDataGenerator;

/// <summary>
/// Which tenant tables the generator covers, which it never will, and which it still owes.
/// lnicoara/cmms#2993.
///
/// The problem this exists to solve is not that tables were missing. It is that nothing knew they were
/// missing. #2876 emitted 12 of 160 tables and recorded the shortfall as a one-line "Out of scope" note, so
/// the gap lived in prose rather than in code: nothing failed, nothing counted, and the first signal was an
/// operator finding empty Preventive Maintenance, Procedures and Inventory screens in pre-prod after a
/// successful load. A dataset that leaves 148 tables empty cannot be told apart from one that covers
/// everything, because both report success.
///
/// So coverage is data, and every table in the EF model must be in exactly one of three states:
///
///   Generated  an emitter writes rows for it
///   Exempt     it CANNOT meaningfully be generated, and the reason is written down here
///   Pending    it should be generated and is not yet
///
/// Two properties follow, and they are the point:
///
///   A table that is in none of the three fails the build. A migration that adds a table cannot slip in
///   unclassified, which is exactly how a 12-of-160 gap grows without anyone deciding it should.
///
///   Pending is a RATCHET. The committed count may fall and may not rise. Adding an emitter is the only
///   way to make progress and there is no way to quietly lose ground, which is what "not required for a
///   load test" turned into last time.
///
/// Exempt is deliberately hard to use. "Not implemented yet" is Pending, not Exempt. A reason has to say
/// why the row could never come from a generator, and every current entry names a mechanism.
/// </summary>
public static class TableCoverage
{
    /// <summary>
    /// Tables no artifact can supply, each with the mechanism that owns the rows instead.
    ///
    /// These are not "hard" tables. They are tables where a generated row would be a lie: live session
    /// state that the app writes and expires, a singleton the provisioner owns, or a row produced only by a
    /// race the generator cannot stage.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>
    {
        ["PresenceBeats"] =
            "live technician presence, written by raw SQL on a heartbeat and expired by time. A generated " +
            "beat is stale the moment it lands and would show phantom users as present.",
        ["UserSessionBeats"] =
            "live session liveness, same heartbeat mechanism as PresenceBeats.",
        ["MobileRefreshTokens"] =
            "issued to a real device at login and hashed. A generated token authenticates nothing, so the " +
            "row would only add rows to a table whose entire purpose is a credential.",
        ["ConflictedWorkOrderWrites"] =
            "written only when two writers collide on the same work order. The row is evidence a race " +
            "happened; fabricating one describes an event that did not occur. The load test PRODUCES these.",
    };

    /// <summary>
    /// Tables the generator owes but does not yet write.
    ///
    /// EMPTY, and that is the point of the issue this landed under. It stays here rather than being deleted
    /// because it is the ratchet: a future table that cannot be synthesized yet lands here with the count
    /// below raised, deliberately and visibly, instead of being dropped on the floor the way 148 tables
    /// were under a one-line "Out of scope" note.
    /// </summary>
    public static readonly IReadOnlySet<string> Pending = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The most tables Pending may hold. Zero: every table in the model is either generated or exempt with
    /// a written reason. Raising this is a decision someone has to make on purpose, in a diff.
    /// </summary>
    public const int PendingCeiling = 0;

    /// <summary>
    /// Tables in the model that are neither generated, exempt, nor pending. Always empty in a healthy
    /// build: a non-empty result means a migration added a table and nobody decided what to do about it.
    /// </summary>
    public static IReadOnlyList<string> Unclassified(
        IReadOnlyList<string> modelTables, IReadOnlyCollection<string> generated)
    {
        var known = new HashSet<string>(generated, StringComparer.Ordinal);
        known.UnionWith(Exempt.Keys);
        known.UnionWith(Pending);
        return modelTables.Where(t => !known.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Tables claimed as generated that the artifact did not actually write. Catches an emitter that runs
    /// but produces nothing at a given profile size, which would otherwise read as coverage.
    /// </summary>
    public static IReadOnlyList<string> ClaimedButEmpty(IReadOnlyDictionary<string, long> tableTotals) =>
        tableTotals.Where(kv => kv.Value == 0)
                   .Select(kv => kv.Key)
                   .OrderBy(t => t, StringComparer.Ordinal)
                   .ToList();
}
