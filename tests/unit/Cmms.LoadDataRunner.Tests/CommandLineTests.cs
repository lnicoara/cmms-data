namespace Cmms.LoadDataRunner.Tests;

// lnicoara/cmms#2917. These exist because --clear-target=demo-health SHIPPED BROKEN.
//
// The parser was inline in Program's top-level statements, where nothing could call it, so the flag was
// only ever checked by reading it. It fell through to a generic name-equals-value path that reflects a
// property whose NAME matches the flag; there is no property called "clear-target" (it is
// ClearTargetSlug), so a real run would have died with "Unknown option: clear-target".
//
// Nothing else caught it. The conformance test asserted the flag was PASSED at every layer, which it was,
// and never that the receiving end could read it. Every hyphenated flag carries the same hazard, so each
// one is pinned here.
public class CommandLineTests
{
    private static LoaderOptions Parse(params string[] args)
    {
        var options = new LoaderOptions();
        var error = CommandLine.Parse(args, options);
        Assert.Null(error);
        return options;
    }

    // The bug, exactly.
    [Fact]
    public void Clear_target_takes_the_tenant_slug_as_its_value()
    {
        var o = Parse("--artifact=/tmp/a", "--tenant=demo-health", "--clear-target=demo-health", "--execute");

        Assert.Equal("demo-health", o.ClearTargetSlug);
        Assert.True(o.Execute);
        Assert.Equal("demo-health", o.Tenant);
        Assert.Equal("/tmp/a", o.Artifact);
    }

    // A bare --clear-target must not read as an implied yes. It names a tenant or it does nothing.
    [Fact]
    public void Clear_target_without_a_slug_is_an_error_rather_than_an_implied_yes()
    {
        var error = CommandLine.Parse(new[] { "--clear-target" }, new LoaderOptions());

        Assert.NotNull(error);
        Assert.Contains("--clear-target=<slug>", error);
    }

    [Fact]
    public void Clear_target_is_absent_unless_asked_for()
    {
        Assert.Equal("", Parse("--artifact=/tmp/a", "--tenant=t", "--execute").ClearTargetSlug);
    }

    [Fact]
    public void Execute_is_off_unless_asked_for()
    {
        Assert.False(Parse("--artifact=/tmp/a", "--tenant=t").Execute);
    }

    [Fact]
    public void Verify_artifact_is_recognised()
    {
        Assert.True(Parse("--artifact=/tmp/a", "--tenant=t", "--verify-artifact").VerifyArtifactOnly);
    }

    // The hazard this pins is the one --clear-target already shipped: the flag is hyphenated and the
    // property is not, so the reflection fallback looks for a property named "count-only", finds none, and
    // the run dies with "Unknown option: count-only" having touched nothing. Reading the parser does not
    // catch that; running it does.
    [Fact]
    public void Count_only_is_recognised()
    {
        Assert.True(Parse("--tenant=t", "--count-only").CountOnly);
    }

    // A census is READ-ONLY, and the absence of a write is the property worth pinning: --count-only must
    // not imply --execute, and it must not carry a clear slug. If either ever became true here, a tool an
    // operator runs to look at a tenant would start changing it.
    [Fact]
    public void Count_only_neither_executes_nor_clears()
    {
        var o = Parse("--tenant=t", "--count-only");

        Assert.True(o.CountOnly);
        Assert.False(o.Execute);
        Assert.Equal("", o.ClearTargetSlug);
    }

    // The reflection path still has to work for the options whose flag and property genuinely agree.
    [Fact]
    public void Name_equals_value_options_still_bind_by_reflection()
    {
        var o = Parse("--artifact=/tmp/a", "--tenant=t", "--BatchSize=250", "--TimeoutSeconds=90");

        Assert.Equal(250, o.BatchSize);
        Assert.Equal(90, o.TimeoutSeconds);
    }

    [Fact]
    public void An_unknown_option_is_refused_rather_than_ignored()
    {
        var error = CommandLine.Parse(new[] { "--nonsense=1" }, new LoaderOptions());

        Assert.NotNull(error);
        Assert.Contains("Unknown option: nonsense", error);
    }

    // A typo in a numeric option must not throw out of the parser and lose the reason.
    [Fact]
    public void A_bad_numeric_value_is_reported_rather_than_thrown()
    {
        var error = CommandLine.Parse(new[] { "--BatchSize=lots" }, new LoaderOptions());

        Assert.NotNull(error);
        Assert.Contains("BatchSize", error);
    }

    // Every hyphenated flag the entrypoint and script can emit, parsed together the way a real run sends
    // them. This is the assertion that would have caught the shipped bug.
    [Fact]
    public void The_full_command_the_operator_runs_parses()
    {
        var o = Parse("--artifact=/artifact", "--tenant=demo-health", "--clear-target=demo-health", "--execute");

        Assert.Equal("/artifact", o.Artifact);
        Assert.Equal("demo-health", o.Tenant);
        Assert.Equal("demo-health", o.ClearTargetSlug);
        Assert.True(o.Execute);
        Assert.False(o.VerifyArtifactOnly);
    }

    // lnicoara/cmms#3055. --clear-only is a stopping point, not a shortcut past the delete fence, so it
    // parses as its own flag and changes nothing about what else is required.
    [Fact]
    public void Clear_only_parses_and_does_not_imply_permission_to_delete()
    {
        var o = new LoaderOptions();
        Assert.Null(CommandLine.Parse(new[] { "--clear-only" }, o));

        Assert.True(o.ClearOnly);
        // Neither of the two yeses is granted by asking for a clear-only run. Program refuses without both,
        // and refuses again unless the slug matches the tenant it resolved from the catalog.
        Assert.False(o.Execute);
        Assert.Equal("", o.ClearTargetSlug);
    }

    [Fact]
    public void Clear_only_is_off_unless_asked_for()
    {
        var o = new LoaderOptions();
        Assert.Null(CommandLine.Parse(new[] { "--execute", "--clear-target=demo-health" }, o));
        Assert.False(o.ClearOnly);
    }
}