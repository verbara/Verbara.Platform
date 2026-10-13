using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.9 (boundary gap 1, design D13): each item of <c>GET /supervisor/sessions/active</c>
/// carries <c>conversationId</c>, the tracked voice conversation of the session's call in the CALLER's tenant
/// (resolved by the call's linked id, as <c>AgentAssistBridge</c> does), or null — always written.
/// </summary>
public sealed class ActiveSessionConversationIdTests
{
    private static readonly TenantId Caller = new("acme");
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();

    private static CallSession Call(string linkedId) => new($"s-{linkedId}", linkedId, "primary", CallDirection.Inbound)
    {
        AgentId = "0651f84b6f284a1c9b8d658330fa8310",
        QueueName = "acme-support",
        ConnectedAt = new DateTimeOffset(2026, 10, 12, 15, 1, 0, TimeSpan.Zero),
    };

    private Conversation Tracked(TenantId tenant, string linkedId)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From("4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a"), TenantId = tenant, ContactId = EntityId.New(),
            Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch, VoiceLinkedId = linkedId,
        };
        _conversations.FindByVoiceLinkedIdAsync(tenant, linkedId, Arg.Any<CancellationToken>()).Returns(conversation);
        return conversation;
    }

    [Fact]
    public async Task GetActiveSessions_ShouldCarryConversationId_WhenTheCallIsTracked()
    {
        var tracked = Tracked(Caller, "1712345678.40");

        var items = await SupervisorEndpoints.ToActiveSessionsAsync(
            [("1712345678.42", Call("1712345678.40"))], Caller, _conversations, CancellationToken.None);

        var item = items.Should().ContainSingle().Subject;
        item.SessionId.Should().Be("1712345678.42");
        item.AgentId.Should().Be("0651f84b6f284a1c9b8d658330fa8310");
        item.QueueName.Should().Be("acme-support");
        item.ConversationId.Should().Be(tracked.ConversationId.Value);
    }

    [Fact]
    public async Task GetActiveSessions_ShouldWriteNullConversationId_WhenNoConversationResolves()
    {
        Tracked(new TenantId("globex"), "1712345699.01");   // tracked only in another tenant

        var items = await SupervisorEndpoints.ToActiveSessionsAsync(
        [
            ("1712345699.07", Call("1712345699.01")),
            ("1712345699.08", Call("1712345699.02")),
            ("1712345699.09", null),
        ], Caller, _conversations, CancellationToken.None);

        items.Select(i => i.ConversationId).Should().AllSatisfy(id => id.Should().BeNull());
        var json = JsonNode.Parse(JsonSerializer.Serialize(items, ApiJsonContext.Default.ActiveSessionDtoArray))!.AsArray();
        json.Should().AllSatisfy(i => i!.AsObject().ContainsKey("conversationId").Should().BeTrue("null is written, never omitted"));
    }

    [Fact]
    public void ActiveSessionDto_ShouldMatchTheFrozenFixture_FieldForField()
    {
        var fixture = SupervisorListenTestKit.Fixture("active-session.v1.json")["body"]!.AsArray();
        var dtos = fixture.Select(n =>
        {
            var o = n!.AsObject();
            string? S(string name) => o[name]?.GetValue<string>();
            return new ActiveSessionDto(
                S("sessionId")!, S("agentId"), S("queueName"), S("callerIdNum"),
                S("connectedAt") is { } at ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : null,
                S("conversationId"));
        }).ToArray();

        var json = JsonNode.Parse(JsonSerializer.Serialize(dtos, ApiJsonContext.Default.ActiveSessionDtoArray))!.AsArray();

        json.Should().HaveCount(fixture.Count);
        for (var i = 0; i < fixture.Count; i++)
            SupervisorListenTestKit.AssertSame(fixture[i]!.AsObject(), json[i]!.AsObject());
    }
}
