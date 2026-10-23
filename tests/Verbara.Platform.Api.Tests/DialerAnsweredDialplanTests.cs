namespace Verbara.Platform.Api.Tests;

/// <summary>
/// dialer-places-calls-end-to-end (design D11 point 1): the shipped <c>docker/asterisk-config/extensions.conf</c> and
/// the demo override <c>docker/demo/demo-overrides/extensions.conf</c> hand an answered campaign leg to the Api's Stasis
/// app from a context of its own, <c>[dialer-answered]</c>, for a digit-only and a <c>+</c>-prefixed number (the Pro
/// builder's <c>Exten</c> is the normalized number, one leading <c>+</c> kept), and both carry the
/// <c>[stasis-queue]</c> context the consumer continues the leg into. The shipped <c>[default]</c> forwards to
/// <c>[dialer-answered]</c>, for campaign rows still on Pro's default context. Precedent:
/// <see cref="OutboundAgentDialplanTests"/>.
/// </summary>
public sealed class DialerAnsweredDialplanTests
{
    private const string ShippedPath = "docker/asterisk-config/extensions.conf";
    private const string DemoPath = "docker/demo/demo-overrides/extensions.conf";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The non-comment, non-blank lines of one dialplan context of the file at <paramref name="relativePath"/>.</summary>
    private static List<string> ContextLines(string relativePath, string name)
    {
        var lines = File.ReadAllLines(Path.Join([RepoRoot(), .. relativePath.Split('/')]));
        var result = new List<string>();
        var inside = false;
        foreach (var line in lines.Select(raw => raw.Split(';', 2)[0].Trim()).Where(line => line.Length > 0))
        {
            if (line.StartsWith('['))
            {
                inside = string.Equals(line, $"[{name}]", StringComparison.Ordinal);
                continue;
            }

            if (inside)
                result.Add(line);
        }

        return result;
    }

    /// <summary>The lines of one extension of <paramref name="context"/>: its <c>exten =&gt;</c> line and its <c>same =&gt;</c> lines.</summary>
    private static List<string> Extension(List<string> context, string exten)
    {
        var result = new List<string>();
        var inside = false;
        foreach (var line in context)
        {
            if (line.StartsWith("exten", StringComparison.Ordinal))
                inside = line.StartsWith($"exten => {exten},", StringComparison.Ordinal);
            if (inside)
                result.Add(line);
        }

        return result;
    }

    [Theory]
    [InlineData(ShippedPath, "_X.")]
    [InlineData(ShippedPath, "_+X.")]
    [InlineData(DemoPath, "_X.")]
    [InlineData(DemoPath, "_+X.")]
    public void ExtensionsConf_ShouldHandTheAnsweredCampaignLegToStasis_WhenShipped(string path, string pattern)
    {
        var lines = Extension(ContextLines(path, "dialer-answered"), pattern);

        lines.Should().Equal(
            [
                $"exten => {pattern},1,Stasis(verbara,campaign,${{DIALER_CAMPAIGN_ID}})",
                "same => n,Hangup()",
            ],
            "{0} hands the answered campaign leg to the Api's Stasis app with its campaign id, for {1} numbers", path, pattern);
    }

    [Theory]
    [InlineData(ShippedPath)]
    [InlineData(DemoPath)]
    public void ExtensionsConf_ShouldContinueIntoTheQueue_WhenShipped(string path)
    {
        var lines = Extension(ContextLines(path, "stasis-queue"), "s");

        lines.Should().Equal(
            ["exten => s,1,Queue(${QUEUE_NAME})", "same => n,Hangup()"],
            "{0} must carry the context StasisInboundConsumer continues the answered leg into", path);
    }

    [Theory]
    [InlineData("_X.")]
    [InlineData("_+X.")]
    public void ExtensionsConf_ShouldForwardDefaultToDialerAnswered_WhenShipped(string pattern)
    {
        var lines = Extension(ContextLines(ShippedPath, "default"), pattern);

        lines.FirstOrDefault().Should().Be(
            $"exten => {pattern},1,Goto(dialer-answered,${{EXTEN}},1)",
            "a campaign row still on Pro's default context reaches [dialer-answered] through the shipped [default]");
    }
}
