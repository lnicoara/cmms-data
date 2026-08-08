namespace Cmms.LoadDataGenerator;

/// <summary>
/// Turns the GENERATOR_PARAMS value into an absolute profile path, or refuses. lnicoara/cmms#2969
///
/// Extracted from Program's top-level statements for the same reason the loader's CommandLine was: a rule
/// that can only be exercised by running the whole binary is a rule nobody tests, and this one guards
/// against producing an artifact that is quietly unloadable.
///
/// The problem it solves is that two things disagree about what a relative path means. File.Exists resolves
/// from the working directory; ConfigurationBuilder resolves from AppContext.BaseDirectory, which is the bin
/// output. The profiles are copied there by the csproj, so a bare "small.json" resolves and
/// "tools/Cmms.LoadDataGenerator/small.json" does not, even though the second one plainly exists and a naive
/// existence check agrees that it does. That gap produced a real artifact stamped with the wrong seed.
/// </summary>
public static class ProfileResolver
{
    public sealed record Result(string? Path, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// Resolves <paramref name="requested"/> against each root in order and returns the first hit as an
    /// absolute path. A null or blank request is not an error: it means no profile was asked for.
    ///
    /// A request that resolves nowhere IS an error, and deliberately so. An implicit default that is absent
    /// is a legitimate bare run; an explicit request that is absent is an operator asking for something they
    /// did not get, and the fallback they would land on carries the full profile's seed.
    /// </summary>
    public static Result Resolve(string? requested, IReadOnlyList<string> roots, string? defaultSeed = null)
    {
        if (string.IsNullOrWhiteSpace(requested)) return new Result(null, null);

        var fallbackSeed = defaultSeed ?? new GeneratorOptions().Seed;

        foreach (var root in roots)
        {
            var candidate = System.IO.Path.IsPathRooted(requested)
                ? requested
                : System.IO.Path.Combine(root, requested);
            if (!File.Exists(candidate)) continue;

            var full = System.IO.Path.GetFullPath(candidate);
            return DeclaresSeed(full, out var why)
                ? new Result(full, null)
                : new Result(null,
                    $"GENERATOR_PARAMS names '{requested}', which resolved to {full} but {why}. A profile " +
                    $"that does not set its own Seed leaves the default \"{fallbackSeed}\" in place, which is " +
                    "the FULL profile's seed, so the artifact would share a key space with the full one and " +
                    "could never be loaded safely. Set Seed explicitly in the profile.");
        }

        return new Result(null,
            $"GENERATOR_PARAMS names '{requested}', which exists under none of: {string.Join(", ", roots)}. " +
            $"Refusing to run on defaults: they carry Seed=\"{fallbackSeed}\", which is the FULL profile's " +
            "seed, so the artifact would share a key space with the full one and could never be loaded safely.");
    }

    /// <summary>
    /// Whether a named profile sets its own non-blank Seed.
    ///
    /// Existence is not enough, which review caught after the first version of this shipped only the
    /// existence check. A profile that is present but inert (an empty object, a misspelled key, a partial
    /// file someone was midway through editing) binds nothing, so the run keeps the default seed and lands
    /// in exactly the poisoned state a missing file would have produced. The failure is identical; only the
    /// route differs, so the guard has to cover both.
    /// </summary>
    private static bool DeclaresSeed(string path, out string why)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                why = "is not a JSON object";
                return false;
            }

            // Case-insensitive, because the configuration binder is and a profile written with a lowercase
            // key binds fine. A check stricter than the binder would reject working profiles.
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "Seed", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                {
                    why = string.Empty;
                    return true;
                }
                why = "declares a blank or non-string Seed";
                return false;
            }

            why = "does not declare a Seed";
            return false;
        }
        catch (System.Text.Json.JsonException ex)
        {
            why = $"is not valid JSON ({ex.Message})";
            return false;
        }
    }
}
