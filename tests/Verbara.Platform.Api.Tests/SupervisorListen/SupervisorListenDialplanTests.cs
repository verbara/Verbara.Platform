using System.Text.RegularExpressions;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 1.3 (design D1): the shipped <c>docker/asterisk-config/extensions.conf</c> carries the
/// <c>[supervisor-listen]</c> context exactly as designed. The ChanSpy options are literals in versioned dialplan, so
/// these assertions pin that no request value can turn a listen into a whisper, a barge or a recording.
/// </summary>
public sealed partial class SupervisorListenDialplanTests
{
    private const string Context = "supervisor-listen";

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

    [GeneratedRegex(@"ChanSpy\(([^,]*),([^)]*)\)")]
    private static partial Regex ChanSpyCall();

    [Fact]
    public void SupervisorListenContext_ShouldDefineTheEndpointAndPhoneExtensions()
    {
        var context = ContextLines(Context);

        context.Should().NotBeEmpty("the [supervisor-listen] context ships in extensions.conf");
        Extension(context, "endpoint").Should().NotBeEmpty();
        Extension(context, "phone").Should().NotBeEmpty();
    }

    [Fact]
    public void EveryChanSpy_ShouldTakeTheSpyTargetAndTheLiteralListenOptions()
    {
        var spies = ContextLines(Context).Where(l => l.Contains("ChanSpy(", StringComparison.Ordinal)).ToList();

        spies.Should().HaveCount(2, "one ChanSpy per extension");
        foreach (var line in spies)
        {
            var match = ChanSpyCall().Match(line);
            match.Success.Should().BeTrue(line);
            match.Groups[1].Value.Should().Be("${SPY_TARGET}");
            match.Groups[2].Value.Should().Be("qESu", "listen-only, quiet, exit with the target, match by uniqueid");
        }
    }

    [Fact]
    public void NoOptionString_ShouldReachWhisperBargeOrRecording()
    {
        var optionStrings = ContextLines(Context)
            .Where(l => l.Contains("ChanSpy(", StringComparison.Ordinal))
            .Select(l => ChanSpyCall().Match(l).Groups[2].Value);
        foreach (var options in optionStrings)
        {
            options.Should().NotContainAny(["w", "W", "B", "r"], "whisper, private whisper, barge and recording are out of scope");
        }
    }

    /// <summary>
    /// Fail closed: <c>ChanSpy()</c> with an empty target spies on every channel, so each extension's first priority
    /// jumps past the spy to a hangup when <c>SPY_TARGET</c> is empty — before the phone leg is even prompted.
    /// </summary>
    [Theory]
    [InlineData("endpoint")]
    [InlineData("phone")]
    public void EveryExtension_ShouldHangUpBeforeChanSpy_WhenTheSpyTargetIsEmpty(string exten)
    {
        var lines = Extension(ContextLines(Context), exten);

        lines.Should().NotBeEmpty();
        lines[0].Should().StartWith($"exten => {exten},1,GotoIf($[\"${{SPY_TARGET}}\" = \"\"]?nospy)",
            "the first priority tests the target before anything else runs");
        var target = lines.FindIndex(l => l.Contains("(nospy),", StringComparison.Ordinal));
        target.Should().BeGreaterThan(0, "the guard jumps to a labelled priority");
        lines[target].Should().EndWith("(nospy),Hangup()", "an empty target hangs up and never reaches the spy");
        lines.Count(l => l.Contains("(nospy)", StringComparison.Ordinal)).Should().Be(1, "one jump target");
    }

    [Fact]
    public void PhoneExtension_ShouldReachChanSpyOnlyAfterTheConfirmationDigitIsOne()
    {
        var phone = Extension(ContextLines(Context), "phone");

        var read = phone.FindIndex(l => l.Contains("Read(LISTEN_CONFIRM,", StringComparison.Ordinal));
        var gate = phone.FindIndex(l => l.Contains("GotoIf($[\"${LISTEN_CONFIRM}\" = \"1\"]?spy)", StringComparison.Ordinal));
        var spy = phone.FindIndex(l => l.Contains("ChanSpy(", StringComparison.Ordinal));

        read.Should().Be(1, "after the empty-target guard, the phone leg reads one confirmation digit");
        gate.Should().BeGreaterThan(read, "then branches to the spy only on 1");
        spy.Should().BeGreaterThan(gate);
        phone[gate + 1].Should().Contain("Hangup()", "any other answer, or none, hangs up before the spy");
        phone[spy].Should().Contain("(spy)", "the spy runs only from the labelled priority the gate jumps to");
    }
}
