using System.Text;
using Cmms.Application.Abstractions;

namespace Cmms.LoadDataRunner;

/// <summary>Records which chunks have landed, durably enough to survive the container that loaded them.</summary>
public interface ICheckpointStore
{
    Task<bool> IsCompleteAsync(string table, int chunk, CancellationToken ct);
    Task MarkCompleteAsync(string table, int chunk, string detail, CancellationToken ct);
}

/// <summary>
/// Checkpoints as one small blob per chunk, keyed by artifact hash AND target database (grill round 2).
///
/// Both halves of that key matter. Without the artifact hash a regenerated artifact inherits the previous
/// one's checkpoints and the loader skips chunks it never loaded. Without the database name, loading the
/// same artifact into a second database looks already-done. Neither failure announces itself.
///
/// It lives here rather than in a table in the tenant database for the reason round 1 settled: tenant
/// schema comes from EF migrations that fan out to every hospital's database, so a checkpoint table would
/// either ship load-test bookkeeping to the whole fleet forever or hand this job the DDL permission it is
/// specifically built not to need.
///
/// One blob per chunk rather than one blob rewritten per chunk: existence IS the record, so there is no
/// read-modify-write to lose a race on.
/// </summary>
public sealed class BlobCheckpointStore : ICheckpointStore
{
    private readonly IBlobStore _blobs;
    private readonly string _container;
    private readonly string _prefix;

    public BlobCheckpointStore(IBlobStore blobs, string container, string artifactHash, string database)
    {
        _blobs = blobs;
        _container = container;
        _prefix = $"{artifactHash}/{Sanitize(database)}";
    }

    private string PathFor(string table, int chunk) => $"{_prefix}/{table}/{chunk:D5}.done";

    public async Task<bool> IsCompleteAsync(string table, int chunk, CancellationToken ct)
    {
        await using var s = await _blobs.GetAsync(_container, PathFor(table, chunk), ct);
        return s is not null;
    }

    public async Task MarkCompleteAsync(string table, int chunk, string detail, CancellationToken ct)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(detail));
        await _blobs.PutAsync(_container, PathFor(table, chunk), content, "application/json", ct);
    }

    // A database name reaches a blob path here, and blob paths do not accept everything a database name may
    // legally contain.
    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
