namespace Cmms.LoadDataRunner;

/// <summary>
/// Parses the runner's command line.
///
/// It lives here, and not inline in Program, because Program is top-level statements and nothing can call
/// into them. That is not a style preference: <c>--clear-target=demo-health</c> shipped broken precisely
/// because the parser was unreachable from a test. The flag fell through to a generic name-equals-value
/// path that reflects a property whose NAME matches the flag, there is no property called
/// "clear-target" (it is <see cref="LoaderOptions.ClearTargetSlug"/>), and the run died with
/// "Unknown option: clear-target" before touching anything.
///
/// Every hyphenated flag has that hazard, so they are all matched explicitly here and the reflection path
/// is left for the plain <c>Name=value</c> options where the flag and the property genuinely do agree.
/// </summary>
public static class CommandLine
{
    /// <summary>Applies <paramref name="args"/> to <paramref name="options"/>, or returns why it could not.</summary>
    public static string? Parse(string[] args, LoaderOptions options)
    {
        foreach (var a in args)
        {
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var body = a[2..];

            if (string.Equals(body, "execute", StringComparison.OrdinalIgnoreCase))
            {
                options.Execute = true;
                continue;
            }

            if (string.Equals(body, "verify-artifact", StringComparison.OrdinalIgnoreCase))
            {
                options.VerifyArtifactOnly = true;
                continue;
            }

            // Empty the tenant and stop. lnicoara/cmms#3055. Still requires --clear-target=<slug> and
            // --execute; this changes what happens AFTER the clear, not whether one is allowed.
            if (string.Equals(body, "clear-only", StringComparison.OrdinalIgnoreCase))
            {
                options.ClearOnly = true;
                continue;
            }

            // Takes the slug as its VALUE, so a wrong tenant cannot be emptied by a flag that merely says
            // "yes". Program compares it against the resolved tenant and refuses on a mismatch.
            if (body.StartsWith("clear-target=", StringComparison.OrdinalIgnoreCase))
            {
                options.ClearTargetSlug = body["clear-target=".Length..];
                continue;
            }
            if (string.Equals(body, "clear-target", StringComparison.OrdinalIgnoreCase))
                return "--clear-target needs the tenant slug you mean to empty: --clear-target=<slug>";

            var kv = body.Split('=', 2);
            if (kv.Length != 2) continue;

            var prop = typeof(LoaderOptions).GetProperty(kv[0],
                System.Reflection.BindingFlags.IgnoreCase |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (prop is null) return $"Unknown option: {kv[0]}";

            try
            {
                object parsed = prop.PropertyType == typeof(int) ? int.Parse(kv[1])
                    : prop.PropertyType == typeof(bool) ? bool.Parse(kv[1])
                    : kv[1];
                prop.SetValue(options, parsed);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                return $"Option {kv[0]} cannot take the value '{kv[1]}': {ex.Message}";
            }
        }
        return null;
    }
}
