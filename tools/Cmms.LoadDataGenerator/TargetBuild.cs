using System.Diagnostics;
using System.Reflection;

namespace Cmms.LoadDataGenerator;

/// <summary>
/// Which build an artifact is FOR, resolved before a row is written. lnicoara/cmms#2993.
///
/// The generator derives its column manifest from the EF model of whatever commit happens to be checked
/// out, which in practice is main. The environment the artifact is destined for is running something older.
/// Nothing reconciled the two, so the operator absorbed the difference by hand, and a schema that had moved
/// since generation surfaced as a bulk-copy failure inside a container job after a multi-gigabyte upload.
///
/// Measured on 2026-08-06, pre-prod's API image was built from b518c627 while its tenant schema had been
/// migrated to d3d1878f, 85 commits apart, because the load script ran the migrate job on every invocation
/// (#2978). So "the build pre-prod runs" had two different answers, and picking either silently would have
/// produced an artifact that fits one and not the other.
///
/// This resolves both and REFUSES when they disagree, rather than guessing. A refusal costs a minute. The
/// alternative costs a generation run, an upload, an image build, and a job execution before the mismatch
/// is reported from inside Azure.
/// </summary>
public static class TargetBuild
{
    public sealed record Resolved(string Commit, string? AppCommit, string? SchemaCommit, string Source);

    /// <summary>The environment this generator exists to feed. Not a parameter.</summary>
    public const string Environment = "preprod";

    /// <summary>
    /// The schema commit pre-prod is running, resolved before a row is written.
    ///
    /// Pre-prod is not a choice here. This is the pre-prod load-test generator, so the target is a fact
    /// about what the tool is FOR, not a setting. It was briefly written as an option with a default, and
    /// the default was "whatever is checked out": the artifact that shipped recorded a branch commit while
    /// pre-prod's schema sat 85 commits behind it. An option nobody has to pass is not a requirement.
    ///
    /// Pinned to the SCHEMA, not the application. A bulk copy has to satisfy the database's columns; the
    /// application commit only decides what can be READ afterwards. Pre-prod runs an application from
    /// b518c627 against a schema migrated to d3d1878f, and an artifact built for that schema loads there
    /// correctly. So a split is reported and generated for; only an unreadable schema is a stop.
    /// </summary>
    public static Resolved ResolveTarget()
    {
        var app = ImageCommit("ca-cmms-api", $"rg-cmms-{Environment}", "cmms-api");
        var schema = ImageCommit("caj-cmms-migrate", $"rg-cmms-{Environment}", "cmms-migrate");

        if (schema is null)
            throw new InvalidOperationException(
                $"cannot read the schema commit for {Environment} (the caj-cmms-migrate image). This " +
                "generator builds artifacts FOR pre-prod, so a pre-prod that cannot be read is a stop. It " +
                "never falls back to whatever happens to be checked out; that is how a dataset built from " +
                "a branch ends up pointed at pre-prod.");

        var source = $"{Environment} schema";
        if (app is not null && !string.Equals(app, schema, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"    NOTE: {Environment} runs application {Short(app)} against schema {Short(schema)}. " +
                "Generating for the SCHEMA, which is what the load must match. The older application " +
                "simply reads a subset of these columns (lnicoara/cmms#2978).");
            source = $"{Environment} schema (application is {Short(app)})";
        }

        // PROVEN, not assumed. The columns in this artifact come from the model compiled into THIS
        // binary, so recording pre-prod's commit in the manifest says nothing on its own. The only thing
        // that makes the label true is that the model here and the model pre-prod runs are the same model.
        //
        // Proven against the PINNED PACKAGE VERSION, not against a git checkout (lnicoara/cmms#3026).
        //
        // This used to diff CmmsDbContextModelSnapshot.cs between pre-prod's commit and HEAD, which
        // silently assumed it was running inside a cmms checkout. It is not any more: this repository has
        // its own history and knows nothing of a cmms commit, so `git rev-parse e7b7d4d0` fails here for a
        // reason that has nothing to do with schema drift. Keeping that check would have meant carrying a
        // cmms checkout around purely to answer a question about a package.
        //
        // The package version is a better answer than the diff ever was. The model in this binary is
        // whatever Cmms.Infrastructure was pinned to and NOTHING else, so a commit-to-commit comparison is
        // exact rather than a proxy, and there is no working tree to be dirty, no branch to be ahead, and
        // no repository to be missing. Version 1.0.0-g<shortsha> carries the commit; that is why cmms
        // publishes it that way.
        var pinned = PinnedCommit();
        if (pinned is null)
            throw new InvalidOperationException(
                "cannot read which cmms commit Cmms.Infrastructure was built from. The generator derives " +
                "every column from that assembly's EF model, so a build that cannot say which commit it " +
                "carries cannot claim its artifact fits any target. Expected an informational version of " +
                "the form 1.0.0-g<shortsha>; see Directory.Build.props (lnicoara/cmms#3026).");

        // Compared on the short prefix, because the two names are produced by different things: the package
        // version carries `git rev-parse --short HEAD` and the image tag carries whatever the deploy
        // stamped. A '-dirty-<hash>' suffix on the image is dropped first, since it describes uncommitted
        // files rather than a different commit.
        var dash = schema.IndexOf("-dirty-", StringComparison.Ordinal);
        var schemaCommit = dash > 0 ? schema[..dash] : schema;
        if (dash > 0)
            Console.WriteLine(
                $"    NOTE: {Environment}'s schema image is tagged '{schema}', built from {Short(schemaCommit)} " +
                "plus uncommitted files. A model change that was uncommitted when that image was built " +
                "cannot be seen from here.");

        if (!SameCommit(pinned, schemaCommit))
            throw new InvalidOperationException(
                $"this build carries cmms {Short(pinned)} but {Environment}'s schema is {Short(schemaCommit)}. " +
                "The artifact would be generated from a model the target database does not have, and the " +
                "bulk copy would fail inside the load job after the upload. Set <CmmsVersion> in " +
                $"Directory.Build.props to 1.0.0-g{Short(schemaCommit)} and rebuild, or promote " +
                $"{Environment} deliberately first. Never migrate the target to fit an artifact " +
                "(lnicoara/cmms#2978).");

        return new Resolved(schema, app, schema, source);
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    /// <summary>
    /// The cmms commit whose EF model is compiled into this build, read off the Cmms.Infrastructure
    /// assembly. lnicoara/cmms#3026.
    ///
    /// Taken from the assembly rather than from $(CmmsVersion) at compile time, because the question is
    /// which model is ACTUALLY loaded. A csproj value says what was asked for; a NuGet restore that
    /// resolved something else, a binding redirect, or a stray assembly on the probing path all say
    /// otherwise, and it is the loaded one that shapes every row. Assert the outcome, not the gate.
    /// </summary>
    private static string? PinnedCommit()
    {
        var v = typeof(Cmms.Infrastructure.Persistence.CmmsDbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) return null;

        // "1.0.0-g7ec5b77c", and sometimes "1.0.0-g7ec5b77c+<buildmeta>" since the SDK appends the source
        // revision id. Take the label between the '-g' and any '+'.
        var g = v.IndexOf("-g", StringComparison.Ordinal);
        if (g < 0) return null;
        var rest = v[(g + 2)..];
        var plus = rest.IndexOf('+');
        var commit = plus >= 0 ? rest[..plus] : rest;
        return commit.Length == 0 ? null : commit;
    }

    /// <summary>
    /// Whether two commit names refer to the same commit, comparing on the shorter one's length.
    ///
    /// The two come from different producers: the package version carries `git rev-parse --short HEAD`
    /// while the image tag carries whatever the deploy stamped, and the two abbreviate to different
    /// lengths. Requiring exact string equality would refuse a matching pair for a formatting difference,
    /// which is the kind of false refusal that gets a guard disabled.
    /// </summary>
    private static bool SameCommit(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        // Seven is git's own default abbreviation, and shorter than that is not a commit name, it is a
        // coincidence waiting to happen.
        return n >= 7 && string.Equals(a[..n], b[..n], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The commit tag on the image a Container App or job is running, resolved through its digest.
    ///
    /// By digest rather than by tag, for the reason the load script already documents: a tag is a mutable
    /// pointer, so "the job runs cmms-load:abc123" is a statement about a name rather than about any
    /// particular image. Returns null rather than throwing, so the caller can report both halves at once.
    /// </summary>
    private static string? ImageCommit(string resource, string resourceGroup, string repository)
    {
        var kind = resource.StartsWith("caj-", StringComparison.Ordinal) ? "job" : "";
        var image = Az($"containerapp {kind} show -n {resource} -g {resourceGroup} " +
                       "--query \"properties.template.containers[0].image\" -o tsv");
        if (image is null) return null;

        var at = image.IndexOf('@');
        if (at < 0) return image[(image.LastIndexOf(':') + 1)..];   // already a tag

        var digest = image[(at + 1)..];
        var tags = Az($"acr manifest list-metadata --registry {RegistryOf(image)} --name {repository} " +
                      $"--query \"[?digest=='{digest}'].tags | [0] | [0]\" -o tsv");
        return string.IsNullOrWhiteSpace(tags) ? null : tags;
    }

    private static string RegistryOf(string image) => image.Split('.')[0];

    private static string? Az(string args) => Run("az", args);

    private static string? Git(string repoRoot, string args) => Run("git", $"-C {repoRoot} {args}");

    private static string? Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && stdout.Length > 0 ? stdout.Split('\n')[0].Trim() : null;
        }
        catch
        {
            // A missing az or git is not a crash: with no environment named the generator still runs
            // offline on a laptop, which is the property that makes it reviewable before anything loads.
            return null;
        }
    }
}
