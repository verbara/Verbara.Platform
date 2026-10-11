using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// An agent reply on a digital channel reaches that channel's connector through the host's channel
/// registry, as the real <c>Program.cs</c> composes it. The WebChat connector resolves the contact's
/// visitor address to a session (spec <c>webchat-visitor-identity</c>); a contact no session on this
/// replica holds is recorded as failed (<c>SESSION_NOT_CONNECTED</c>) — never thrown as
/// "no connector registered". Delivery to a connected socket is covered by
/// <c>WebChatVisitorIdentityTests</c>.
/// </summary>
public sealed class DigitalChannelConnectorTests(PlatformApiFactory factory) : IClassFixture<PlatformApiFactory>
{
    private static readonly TenantId Tenant = new("t-connector-webchat");

    [Fact]
    public async Task SendMessageAsync_ShouldNotThrowNoConnector_WhenContactHasWebChatAddress()
    {
        var services = factory.Services;
        var agentId = EntityId.From($"agent-{Guid.NewGuid():N}");
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        contact.AddAddress(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));
        await services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);

        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(agentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);

        var conversations = services.GetRequiredService<IConversationService>();
        var send = async () => await conversations.SendMessageAsync(
            conversation.ConversationId,
            Tenant,
            new MessageEnvelope([new TextBlock("hello from the agent")]),
            agentId,
            ConversationOwnerKind.Agent,
            CancellationToken.None);

        var message = (await send.Should().NotThrowAsync()).Subject;
        message.DeliveryStatus.Should().Be(
            MessageDeliveryStatus.Failed,
            "the connector was reached and refused because no visitor session is connected");
    }

    [Fact]
    public void GetConnector_ShouldResolveWebChatConnector_WhenHostComposesTheRegistry()
    {
        var registry = factory.Services.GetRequiredService<IChannelRegistry>();

        registry.GetConnector(ChannelType.WebChat).Channel.Should().Be(ChannelType.WebChat);
        registry.AvailableChannels.Should().Contain(ChannelType.WebChat);
    }
}
