namespace Verbara.Platform.Api.Tests;

/// <summary>
/// outbound-calls-reach-the-trunk (design D5, D6, D7): the shipped <c>docker/asterisk-config/extensions.conf</c> carries
/// the <c>[outbound-agent]</c> context exactly as designed. Click-to-dial and the external transfer both enter it, so
/// these assertions pin the dial form that reaches a trunk (<c>PJSIP/t-{id}/sip:{number}@{host}</c>), the
/// <c>+</c>-prefixed pattern, and the caller-ID name the carrier sees.
/// </summary>
public sealed class OutboundAgentDialplanTests
{
    private const string Context = "outbound-agent";
    private const string DialLine = "same => n,Dial(PJSIP/${TRUNK}/sip:${EXTEN}@${TRUNK_HOST})";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The non-comment, non-blank lines of one dialplan context.</summary>
    private static List<string> ContextLines(string name)
    {
        var lines = File.ReadAllLines(Path.Join(RepoRoot(), "docker", "asterisk-config", "extensions.conf"));
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
    [InlineData("_X.")]
    [InlineData("_+X.")]
    public void OutboundAgentExtension_ShouldPresentTheCallerIdAndDialTheTrunkHost_WhenThePatternMatches(string pattern)
    {
        var lines = Extension(ContextLines(Context), pattern);

        lines.Should().Equal(
            [
                $"exten => {pattern},1,Set(CALLERID(num)=${{OUTBOUND_CALLERID}})",
                "same => n,Set(CALLERID(name)=${OUTBOUND_CALLERID})",
                DialLine,
                "same => n,Hangup()",
            ],
            "each pattern presents the tenant caller ID as number and name, dials the endpoint id with an explicit sip: URI to the trunk host, and hangs up");
    }

    [Fact]
    public void OutboundAgentContext_ShouldDefineExactlyTheDigitAndPlusPatterns()
    {
        var extens = ContextLines(Context).Where(l => l.StartsWith("exten", StringComparison.Ordinal)).ToList();

        extens.Should().HaveCount(2, "one extension for a number of digits and one for a number with a leading +");
        extens.Should().Contain(l => l.StartsWith("exten => _X.,", StringComparison.Ordinal));
        extens.Should().Contain(l => l.StartsWith("exten => _+X.,", StringComparison.Ordinal));
    }

    [Fact]
    public void OutboundAgentContext_ShouldNeverDialTheTrunkWithABareExten()
    {
        var context = ContextLines(Context);

        context.Should().NotBeEmpty("the [outbound-agent] context ships in extensions.conf");
        context.Should().NotContain(l => l.Contains("PJSIP/${TRUNK}/${EXTEN}", StringComparison.Ordinal),
            "chan_pjsip reads what follows endpoint/ as a full SIP URI, so a bare number never reaches the trunk");
    }
}
