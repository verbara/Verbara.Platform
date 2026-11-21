using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// supervisor-whisper-reaches-the-agent (design D3, task 1.4): the whisper's <c>agentassist.suggestion</c> is built from the
/// session's live record, addressed to its bound agent, and keeps the suggestion wire of
/// <c>agentassist-events.v1.json</c> <c>events."agentassist.suggestion"</c>.
/// </summary>
public sealed class SupervisorWhisperEventTests
{
    /// <summary>The members of the fixture's <c>wire</c>, in its order; <c>triggerPhrase</c> is absent.</summary>
    private static readonly string[] WireMembers =
    [
        "sessionId", "agentId", "conversationId", "suggestionId", "text", "priority", "source", "tenantId", "type",
        "timestamp", "eventType", "metadata",
    ];

    private static LiveSessionRecord Record(string? agentId = "a-9e8d7c6b5a493827") =>
        new("as-5c4b3a29", "demo", agentId, "support", "5551234", null, "1712345678.90");

    private static AgentAssistSuggestionEvent Build() =>
        SupervisorWhisper.BuildEvent(Record(), "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", "Offer the retention discount now.", "sw-0123");

    private static async Task<string> SseFrame(PlatformEvent evt)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await SseEndpoints.WriteEventAsync(context.Response, evt.Type, evt, CancellationToken.None);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    [Fact]
    public void BuildEvent_ShouldCarryTheRecordsIdentityAndTheWhisperValues()
    {
        var evt = Build();

        evt.TenantId.Should().Be("demo", "the tenant the record is filed under");
        evt.SessionId.Should().Be("as-5c4b3a29");
        evt.AgentId.Should().Be("a-9e8d7c6b5a493827", "the record's bound agent");
        evt.ConversationId.Should().Be("4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a");
        evt.SuggestionId.Should().Be("sw-0123");
        evt.Text.Should().Be("Offer the retention discount now.");
        evt.Priority.Should().Be("Critical");
        evt.Source.Should().Be("Supervisor");
        evt.TriggerPhrase.Should().BeNull();
        evt.Type.Should().Be("agentassist.suggestion");
        evt.Metadata.TenantId.Should().Be("demo", "the envelope tenant the cross-pod receiver compares to the payload's");
    }

    [Fact]
    public void BuildEvent_ShouldAddressTheBoundAgent_WhateverAgentTheRecordNames()
    {
        foreach (var agentId in new[] { "a-9e8d7c6b5a493827", "a-1111111111111111" })
        {
            var evt = SupervisorWhisper.BuildEvent(Record(agentId), "c-1", "hi", "sw-1");

            evt.Audience.Should().Be(ConversationAudience.ForAgent(agentId),
                "the audience is computed in the constructor from the bound agent, never copied with `with`");
        }
    }

    [Fact]
    public void NewSuggestionId_ShouldBeANewSwPrefixedIdEachTime()
    {
        var first = SupervisorWhisper.NewSuggestionId();
        var second = SupervisorWhisper.NewSuggestionId();

        first.Should().StartWith("sw-").And.HaveLength(3 + 32);
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task WriteEventAsync_ShouldWriteTheFixturesSuggestionMembersInOrder_WithNoTriggerPhrase()
    {
        var frame = await SseFrame(Build());

        frame.Should().StartWith("event: agentassist.suggestion\ndata: ").And.EndWith("\n\n");
        var data = JsonNode.Parse(frame["event: agentassist.suggestion\ndata: ".Length..].TrimEnd('\n'))!.AsObject();
        data.Select(p => p.Key).Should().Equal(WireMembers, "agentassist-events.v1.json events.\"agentassist.suggestion\" wire, in order");
        data.ContainsKey("triggerPhrase").Should().BeFalse();
        data["priority"]!.GetValue<string>().Should().Be("Critical");
        data["source"]!.GetValue<string>().Should().Be("Supervisor");
        data["metadata"]!["tenantId"]!.GetValue<string>().Should().Be("demo");
    }

    [Fact]
    public void Decode_ShouldGiveBackEveryMemberAndTheAudience_WhenTheWhisperCrossesTheBackplane()
    {
        var original = Build();

        var payload = JsonSerializer.Serialize(original, original.GetType(), PushRelayPayloadSerialization.CreateOptions());
        var decoded = JsonSerializer.Deserialize(payload, PlatformPushJsonContext.Default.AgentAssistSuggestionEvent)!;

        decoded.Should().BeEquivalentTo(original, o => o.ComparingRecordsByMembers(), "every member crosses the backplane");
        decoded.Timestamp.Should().Be(original.Timestamp);
        decoded.Audience.Should().Be(original.Audience, "the audience is recomputed from the decoded agent id");
    }
}
