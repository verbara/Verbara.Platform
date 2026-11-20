using Verbara.Platform.E2E.Harness.Hub;

namespace Verbara.Platform.E2E.Harness.Scenarios;

/// <summary>
/// Offline negative controls for <see cref="ConversationFramesScenario.Evaluate"/>: synthetic client records with a
/// known verdict, run with <c>HARNESS_SCENARIO=conversation-frames-selfcheck</c> and no lab. Each case must get the
/// verdict it declares; a case that does not shows the evaluator cannot tell that record apart from a pass.
/// </summary>
internal static class ConversationFramesSelfCheck
{
    public const string Name = "conversation-frames-selfcheck";

    private const string WebChatId = "webchat-1";
    private const string VoiceId = "voice-1";

    /// <summary>Runs every case; returns the failures (empty when every verdict is the declared one).</summary>
    public static IReadOnlyList<string> Run()
    {
        var workload = new WorkloadResult(true, [WebChatId], [VoiceId], [], "{}");
        var webChat = Frames(WebChatId, ConversationFramesScenario.DeclaredWebChat);
        var voice = Frames(VoiceId, ConversationFramesScenario.DeclaredVoicePrefix);
        var timedClose = Frames(VoiceId, [new FrameTransition("WrapUp", "Closed")]);

        (string Case, IReadOnlyList<ConversationFrame> Frames, bool ExpectedPass)[] cases =
        [
            ("declared frames only", [.. webChat, .. voice], true),
            ("declared frames plus the voice call's timed close", [.. webChat, .. voice, .. timedClose], true),
            ("no frames", [], false),
            ("voice frame missing", [.. webChat, .. voice.Take(2)], false),
            ("WebChat frame duplicated", [.. webChat, .. webChat.Take(1), .. voice], false),
            ("voice WrapUp frame duplicated", [.. webChat, .. voice, .. voice.Skip(2)], false),
            ("voice timed close duplicated", [.. webChat, .. voice, .. timedClose, .. timedClose], false),
            ("voice frame after the declared three that is not the timed close", [.. webChat, .. voice, .. Frames(VoiceId, [new FrameTransition("WrapUp", "Active")])], false),
        ];

        var failures = new List<string>();
        foreach (var (name, frames, expectedPass) in cases)
        {
            var record = ConversationFramesScenario.Evaluate("selfcheck#0", "selfcheck", frames, workload, 0);
            if (record.DeclaredFramesPassed != expectedPass)
            {
                failures.Add($"Case '{name}': expected {(expectedPass ? "PASS" : "FAIL")}, got {(record.DeclaredFramesPassed ? "PASS" : "FAIL")}.");
            }
        }

        return failures;
    }

    private static List<ConversationFrame> Frames(string conversationId, IEnumerable<FrameTransition> transitions) =>
        transitions.Select(t => new ConversationFrame(conversationId, t.PreviousState, t.NewState, DateTimeOffset.UnixEpoch)).ToList();
}
