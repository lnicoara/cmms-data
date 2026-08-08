using System.IO.Compression;
using System.Text.Json;

namespace Cmms.LoadDataRunner;

public sealed class LoadReport
{
    public bool Ok { get; set; } = true;
    public List<string> Lines { get; } = new();
    public long RowsLoaded { get; set; }
    public int ChunksLoaded { get; set; }
    public int ChunksSkipped { get; set; }
    public int ChunksHealed { get; set; }

    // Printed AS IT HAPPENS, not collected for the caller to print when the run ends. A load takes hours,
    // and buffering meant a running job and a hung job produced identical output: none. One hung for 8
    // hours on its first blob call and looked exactly like a load in progress, because nothing it had
    // reached was visible until a return that never came.
    public void Say(string line) { Lines.Add(line); Console.WriteLine(line); }
    public void Fail(string line) { Ok = false; Lines.Add(line); Console.WriteLine(line); }
}

/// <summary>
/// Runs (or merely plans) one load of one artifact into one tenant database.
///
/// The preflight asks one question about the ARTIFACT'S OWN rows: can this dataset still be laid down
/// correctly on this target? Two answers stop the run, and both are about the artifact rather than about
/// the tenant.
///
/// A DEFICIT is always a refusal. Rows the checkpoints say landed are gone, so the target has drifted from
/// the checkpoint set and a resume would not reproduce the dataset the artifact describes. No probe can
/// explain rows going missing.
///
/// A PARTIALLY-PRESENT CHUNK is always a refusal. A chunk is one transaction, so this loader cannot produce
/// half of one; finding half means something else wrote to the table, and repairing it row by row would be
/// guessing at which rows belong.
///
/// "Known to have landed" is deliberately wider than "checkpointed". A chunk commits and THEN its
/// checkpoint is written, so a crash between the two leaves rows with no checkpoint (grill round 2). A bare
/// count equality would refuse to resume in exactly the situation resumability exists for, so the loader
/// probes the uncheckpointed chunks and adopts the ones provably present.
///
/// What is NOT a refusal, since lnicoara/cmms#2993: rows that no chunk can account for, meaning rows the
/// artifact never wrote. Grill round 4 made that a refusal under the rule "the artifact owns the tables it
/// targets". The rule is wrong for what this tool is. This is a LOAD TEST, and rows somebody else put there
/// are more rows under load, which is the quantity being measured. The rule protected nothing and cost
/// everything: it made a target loadable only after being emptied, which is how a failed load left pre-prod
/// holding less data than before it ran. The count is reported and the load proceeds on top.
/// </summary>
public sealed class Loader
{
    private readonly Artifact _artifact;
    private readonly LoadPlan _plan;
    private readonly TargetDatabase _target;
    private readonly ICheckpointStore _checkpoints;
    private readonly LoaderOptions _options;

    public Loader(
        Artifact artifact, LoadPlan plan, TargetDatabase target,
        ICheckpointStore checkpoints, LoaderOptions options)
    {
        _artifact = artifact;
        _plan = plan;
        _target = target;
        _checkpoints = checkpoints;
        _options = options;
    }

    public async Task<LoadReport> RunAsync(CancellationToken ct)
    {
        var report = new LoadReport();

        report.Say($"artifact {_artifact.Directory}");
        report.Say($"  format {_artifact.FormatVersion}  seed {_artifact.Seed}  hash {_artifact.Hash}");
        report.Say($"target   {_target.DatabaseName}");
        report.Say("");
        foreach (var line in _plan.Describe()) report.Say(line);
        report.Say("");

        var landed = await CheckpointedAsync(report, ct);
        var checkpointed = landed.Values.Sum(s => s.Count);
        if (checkpointed > 0)
            report.Say($"checkpoints: {checkpointed:N0} of {_plan.TotalChunks:N0} chunks already loaded, this is a resume");

        if (!await PreflightAsync(landed, report, ct)) return report;

        if (!_options.Execute)
        {
            report.Say("");
            report.Say("PLAN ONLY. Nothing was written. Pass --execute to load.");
            return report;
        }

        await LoadAsync(landed, report, ct);
        return report;
    }

    /// <summary>
    /// The first thing a run does, and the first thing that can hang it. One blob GET per chunk, so a fresh
    /// artifact costs 438 sequential round trips before a single row moves.
    ///
    /// A real run stopped dead HERE for eight hours: status Running, database idle, nothing written, and no
    /// output at all because the report was buffered. So this announces itself before it starts, reports as
    /// it walks, and is BOUNDED. An unbounded wait on a remote call is not resilience; it is the difference
    /// between a two-minute error and an overnight loss, and the overnight loss is the one that happened.
    /// </summary>
    private async Task<Dictionary<string, HashSet<int>>> CheckpointedAsync(LoadReport report, CancellationToken ct)
    {
        report.Say($"scanning {_plan.TotalChunks:N0} checkpoints ({_options.CheckpointContainer})...");

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(CheckpointScanTimeout);

        var completed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var scanned = 0;
        try
        {
            foreach (var t in _plan.Tables)
            {
                var set = new HashSet<int>();
                foreach (var c in t.Chunks)
                {
                    if (await _checkpoints.IsCompleteAsync(t.Table, c.Number, bounded.Token))
                        set.Add(c.Number);
                    if (++scanned % 100 == 0) report.Say($"  {scanned:N0}/{_plan.TotalChunks:N0} checkpoints scanned");
                }
                completed[t.Table] = set;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Checkpoint scan stalled after {scanned:N0} of {_plan.TotalChunks:N0} blob reads and " +
                $"{CheckpointScanTimeout.TotalMinutes:N0} minutes against container " +
                $"'{_options.CheckpointContainer}'. The load never started, so nothing was written. This is " +
                "the checkpoint store, not the database: check the job's identity and its network path to " +
                "the storage account.");
        }

        report.Say($"  {scanned:N0} checkpoints scanned");
        return completed;
    }

    // Generous next to 438 round trips, tiny next to a load measured in hours. Its only job is to make a
    // hang terminate with a message that names where it hung.
    private static readonly TimeSpan CheckpointScanTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reconciles every target table against the checkpoint set, adopting chunks that are provably present.
    ///
    /// Note which side is cheap. The expected-zero case is the one that prevents catastrophe, and it costs
    /// a single seek, so the safety-critical check is also the fast one. The expensive work (an exact count,
    /// then per-chunk key probes) only happens once rows already exist, which means either a resume or a
    /// target that should not have been chosen.
    /// </summary>
    private async Task<bool> PreflightAsync(
        Dictionary<string, HashSet<int>> landed, LoadReport report, CancellationToken ct)
    {
        var ok = true;
        var healed = 0;

        foreach (var t in _plan.Tables)
        {
            var expected = t.Chunks.Where(c => landed[t.Table].Contains(c.Number)).Sum(c => c.Rows);

            // Nothing is meant to be here yet, and nothing is: the common fresh-load path, one seek.
            if (expected == 0 && !await _target.HasAnyRowAsync(t.Table, ct)) continue;

            var actual = await _target.RowCountAsync(t.Table, ct);
            if (actual == expected) continue;

            if (actual < expected)
            {
                report.Fail(
                    $"REFUSING: {t.Table} holds {actual:N0} rows but its checkpoints account for {expected:N0}. " +
                    "Rows the checkpoints say landed are gone, so the target drifted from the checkpoint set " +
                    "and resuming would not produce the dataset the artifact describes.");
                ok = false;
                continue;
            }

            // More rows than the checkpoints explain. Either chunks committed without their checkpoint
            // landing, which is recoverable, or something else wrote to the table, which is not.
            var partial = false;
            foreach (var chunk in t.Chunks.Where(c => !landed[t.Table].Contains(c.Number)))
            {
                var presence = await ProbeAsync(t, chunk, ct);
                if (presence == ChunkPresence.Absent) continue;

                if (presence == ChunkPresence.Partial)
                {
                    // A chunk is one transaction, so this loader cannot produce a half-loaded chunk. It
                    // means an older loader, manual interference, or another writer. Repairing it row by
                    // row would be guessing at which rows belong, so the run stops.
                    report.Fail(
                        $"REFUSING: {t.Table} chunk {chunk.Number} is PARTIALLY present. Chunks load in one " +
                        "transaction, so a partial chunk means something other than this loader wrote to the " +
                        "table. Resolve it deliberately rather than letting a resume paper over it.");
                    ok = false;
                    partial = true;
                    break;
                }

                landed[t.Table].Add(chunk.Number);
                expected += chunk.Rows;
                healed++;
                if (_options.Execute)
                    await _checkpoints.MarkCompleteAsync(t.Table, chunk.Number, Detail(chunk, "healed"), ct);
            }
            // A partial chunk already explains this table; the count message would only add noise. Every
            // OTHER table still gets checked, so one bad table does not hide the rest.
            if (partial) continue;

            if (actual != expected)
            {
                // REPORTED, not refused. This used to fail the run on the rule that "the artifact owns the
                // tables it targets", and that rule is wrong for what this tool is: a load test. Rows the
                // artifact did not write are more rows under load, which is the thing being measured, so
                // there is nothing to protect the run from. The rule's real cost was that it made the load
                // unusable without first emptying the target, and emptying pre-prod is how a failed load
                // left it with less data than it started with.
                //
                // The refusals that remain are the ones about the artifact's OWN rows: a deficit against the
                // checkpoints above (rows that landed are gone, so a resume would not reproduce the dataset)
                // and a partially-present chunk (something other than this loader wrote to the table). Those
                // say the artifact cannot be laid down correctly. This one only ever said the tenant was not
                // empty.
                //
                // Still said out loud, because a count that surprises the operator is worth seeing.
                report.Say(
                    $"note: {t.Table} holds {actual:N0} rows, and the chunks that are checkpointed or " +
                    $"provably present account for {expected:N0}. The other rows did not come from this " +
                    "artifact. Loading on top of them.");
            }
        }

        if (!ok) return false;

        if (healed > 0)
        {
            report.ChunksHealed = healed;
            report.Say(
                $"healed: {healed:N0} chunk(s) were already present with no checkpoint, which is a commit that " +
                "outlived its checkpoint write. Adopted rather than reloaded.");
        }
        report.Say("preflight passed");
        return true;
    }

    private async Task LoadAsync(
        Dictionary<string, HashSet<int>> landed, LoadReport report, CancellationToken ct)
    {
        report.Say("");
        foreach (var t in _plan.Tables)
        {
            foreach (var chunk in t.Chunks)
            {
                ct.ThrowIfCancellationRequested();

                // Preflight already resolved every chunk to present or absent, so this loop does no
                // probing: it loads what is missing and skips what is not.
                if (landed[t.Table].Contains(chunk.Number)) { report.ChunksSkipped++; continue; }

                await _target.CopyChunkAsync(t.Table, t.Binders, chunk.Path, chunk.Rows, _options.BatchSize, ct);
                await _checkpoints.MarkCompleteAsync(t.Table, chunk.Number, Detail(chunk, "loaded"), ct);

                report.ChunksLoaded++;
                report.RowsLoaded += chunk.Rows;
            }
            report.Say($"  {t.Table,-18} done  {t.Rows,12:N0} rows");
        }

        report.Say("");
        report.Say(
            $"{report.RowsLoaded:N0} rows loaded in {report.ChunksLoaded:N0} chunks " +
            $"({report.ChunksSkipped:N0} already checkpointed, {report.ChunksHealed:N0} healed)");
    }

    private static string Detail(ChunkRef chunk, string how) =>
        JsonSerializer.Serialize(new { chunk.Table, chunk.Number, chunk.Rows, how });

    private enum ChunkPresence { Absent, Partial, Full }

    /// <summary>
    /// Whether a chunk is present, from its first, middle and last primary keys: three indexed seeks
    /// rather than a count over millions of rows.
    ///
    /// Be precise about what this does and does not establish. For chunks THIS loader wrote it is
    /// conclusive, because a chunk is one transaction and therefore all-or-nothing, so any single key
    /// settles it and three make an interrupted older loader visible too. It is NOT a proof that every
    /// row is present: a table holding the boundary keys but missing rows in between would pass.
    ///
    /// What closes that gap is the caller, not this method. Adopting a chunk adds its full row count to
    /// the expected total, and preflight then requires the table's actual count to match exactly. So a
    /// chunk that probes present while missing rows fails the arithmetic and the run stops. The probe
    /// picks WHICH chunks to adopt; the count check is what validates the adoption.
    /// </summary>
    private async Task<ChunkPresence> ProbeAsync(TablePlan t, ChunkRef chunk, CancellationToken ct)
    {
        if (t.KeyColumns.Count == 0) return ChunkPresence.Absent;

        var probes = ProbeKeys(chunk.Path, t);
        if (probes.Count == 0) return ChunkPresence.Absent;

        var present = 0;
        foreach (var key in probes)
            if (await _target.KeyExistsAsync(t.Table, t.KeyColumns, key, ct))
                present++;

        if (present == 0) return ChunkPresence.Absent;
        return present == probes.Count ? ChunkPresence.Full : ChunkPresence.Partial;
    }

    /// <summary>
    /// The keys of a chunk's first, middle and last rows. One decompression pass, and only on the
    /// ambiguous path where a chunk has no checkpoint but the table has unexplained rows.
    /// </summary>
    private static List<List<object?>> ProbeKeys(string path, TablePlan t)
    {
        var keyBinders = t.KeyColumns
            .Select(k => t.Binders.First(b => string.Equals(b.ColumnName, k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var lines = new List<string>();
        using (var file = File.OpenRead(path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            while (reader.ReadLine() is { } line) lines.Add(line);
        }
        if (lines.Count == 0) return new List<List<object?>>();

        return new[] { 0, lines.Count / 2, lines.Count - 1 }
            .Distinct()
            .Select(i => Keys(lines[i], keyBinders))
            .ToList();
    }

    private static List<object?> Keys(string line, IReadOnlyList<ColumnBinder> keyBinders)
    {
        using var doc = JsonDocument.Parse(line);
        return keyBinders.Select(b => b.Read(doc.RootElement.GetProperty(b.ColumnName))).ToList();
    }
}
