using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-console-contract-debt (C4): where a stored note must not go, and where it must. A note is internal, so no
/// channel connector and no visitor's socket gets it; it is still data held about the contact, so the personal-data
/// export holds it and the purge removes it. All of this holds without a line of code for it, and these tests pin it
/// against the next change that sends a stored message or filters one.
/// </summary>
public sealed class TransferNoteLeakTests : IClassFixture<TransferNoteApiFactory>
{
    private const string Note = "Customer disputes the October invoice; please do not re-issue it.";

    private readonly TransferNoteApiFactory _factory;

    public TransferNoteLeakTests(TransferNoteApiFactory factory) => _factory = factory;

    private static string Route(Conversation conversation, string action) =>
        ConversationOwnershipEndpointTests.Route(conversation, action);

    private (User User, Conversation Conversation) SeedOwnedOnWhatsApp()
    {
        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.SeedConversation(
            ConversationOwner.ForAgent(agent.AgentId), ConversationState.Active, channel: ChannelType.WhatsApp);
        _factory.SeedContactOf(conversation, $"+57300{Guid.NewGuid().GetHashCode() & 0xFFFFFF}");
        return (user, conversation);
    }

    private async Task ShouldHaveNoSendAsync(Conversation conversation) =>
        await _factory.WhatsAppConnector.DidNotReceive().SendAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == conversation.ConversationId),
            Arg.Any<CancellationToken>());

    // ─── "A note SHALL never reach the customer" ─────────────────────────────

    [Fact]
    public async Task Transfer_ShouldSendNothingThroughTheConnector_WhenAQueueTransferCarriesANote()
    {
        var (user, conversation) = SeedOwnedOnWhatsApp();
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle(m => m.Direction == MessageDirection.System);
        await ShouldHaveNoSendAsync(conversation);
    }

    [Fact]
    public async Task Transfer_ShouldSendNothingThroughTheConnector_WhenAnAgentTransferCarriesANote()
    {
        var (user, conversation) = SeedOwnedOnWhatsApp();
        var (_, target) = _factory.NewAgent("target");
        _factory.SeedMessages(conversation, 1); // opens the WhatsApp window the reply below needs
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetAgentId = target.AgentId.Value, note = Note });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().ContainSingle(m => m.Direction == MessageDirection.System);
        await ShouldHaveNoSendAsync(conversation);

        // The control: this conversation does reach that connector, and the reply is the only thing it ever got.
        const string reply = "Hello, I have your case now";
        await _factory.Services.GetRequiredService<IConversationService>().SendMessageAsync(
            conversation.ConversationId,
            TransferNoteApiFactory.Tenant,
            new MessageEnvelope([new TextBlock(reply)]),
            target.AgentId,
            ConversationOwnerKind.Agent,
            CancellationToken.None);

        var sends = _factory.WhatsAppConnector.ReceivedCalls()
            .Select(call => call.GetArguments().OfType<OutboundMessage>().SingleOrDefault())
            .Where(message => message?.ConversationId == conversation.ConversationId)
            .ToList();
        sends.Should().ContainSingle()
            .Which!.Content.Blocks.OfType<TextBlock>().Should().ContainSingle()
            .Which.Text.Should().Be(reply);
    }

    [Fact]
    public async Task Transfer_ShouldSendTheVisitorNoFrame_WhenAWebChatTransferCarriesANote()
    {
        _factory.ActivateWebChat();
        using var anonymous = _factory.CreateClient();
        using var created = await anonymous.PostAsJsonAsync(
            "/api/v1/webchat/sessions", new { tenantId = TransferNoteApiFactory.Tenant.Value });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var sessionId = body.RootElement.GetProperty("sessionId").GetString()!;
        var session = await _factory.Services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId);
        session.Should().NotBeNull();

        var (user, agent) = _factory.NewAgent("owner");
        var conversation = _factory.Load(session!.ConversationId)!;
        conversation.State = ConversationState.Active;
        conversation.Owner = ConversationOwner.ForAgent(agent.AgentId);
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wsClient = _factory.Server.CreateWebSocketClient();
        using var socket = await wsClient.ConnectAsync(new Uri($"ws://localhost/ws/webchat/{sessionId}"), timeout.Token);
        (await ReceiveTextAsync(socket, timeout.Token)).Should().Contain("connected");

        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);
        var response = await client.PostAsJsonAsync(
            Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value, note = Note });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.MessagesOf(conversation.ConversationId).Should().Contain(m => m.Direction == MessageDirection.System);

        // Frames arrive in order on one socket, so a marker sent now is a fence: whatever the transfer put on the
        // socket reads before it, and "the first frame is the marker" needs no wait that a slow run could outlast.
        const string marker = "fence-after-the-transfer";
        var sent = await _factory.Services.GetRequiredService<IWebChatTransport>()
            .SendToClientAsync(sessionId, new MessageEnvelope([new TextBlock(marker)]), timeout.Token);
        sent.Should().BeTrue();

        var frame = await ReceiveTextAsync(socket, timeout.Token);
        frame.Should().Contain(marker, "the transfer put nothing on the visitor's socket before the marker");
    }

    // ─── "Data-subject tools SHALL keep treating a note as conversation data" ─

    [Fact]
    public async Task ExportThenPurge_ShouldHoldTheNoteThenRemoveIt_WhenTheContactsConversationHoldsOne()
    {
        var (user, conversation) = SeedOwnedOnWhatsApp();
        var queue = _factory.SeedQueue();
        using var client = _factory.ClientFor(user);
        (await client.PostAsJsonAsync(Route(conversation, "transfer"), new { targetQueueId = queue.QueueId.Value, note = Note }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = _factory.ClientFor(_factory.NewUser(UserRole.Admin, "admin"));
        var contactId = conversation.ContactId.Value;

        var export = await admin.PostAsJsonAsync("/api/v1/admin/gdpr/export", new { contactId });

        var exported = await export.Content.ReadAsStringAsync();
        export.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {exported}");
        using (var document = JsonDocument.Parse(exported))
        {
            var message = document.RootElement.GetProperty("messages").EnumerateArray().Should().ContainSingle().Subject;
            message.GetRawText().Should().Contain(Note, "the export reports what is held about the person");
        }

        var purge = await admin.PostAsJsonAsync("/api/v1/admin/gdpr/purge", new { contactId, reason = "Subject erasure request" });

        purge.StatusCode.Should().Be(HttpStatusCode.OK, because: await purge.Content.ReadAsStringAsync());
        _factory.MessagesOf(conversation.ConversationId).Should().BeEmpty("the purge removes a note with the other messages");
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var result = await socket.ReceiveAsync(buffer, ct);
        return result.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(buffer, 0, result.Count)
            : null;
    }
}
