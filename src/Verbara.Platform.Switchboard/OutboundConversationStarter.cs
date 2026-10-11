using Microsoft.Extensions.Logging;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// RED-PHASE STUB (whatsapp-works-for-real task 7.2) — replaced by the design D14 flow in task 7.3.
/// It reproduces the pre-fix <c>POST /conversations</c> path with <c>initialMessage</c> literally: get the
/// contact's open conversation or persist a new one <see cref="ConversationState.Queued"/> with no owner,
/// then send as the agent, which throws when the agent does not own it (leaving the new conversation
/// behind as an orphan). The constructor is the one the real starter needs, so the tests written against it
/// do not change when the flow is replaced.
/// </summary>
internal sealed partial class OutboundConversationStarter : IOutboundConversationStarter
{
    private readonly IConversationStore _conversations;
    private readonly IContactStore _contacts;
    private readonly IMessageStore _messages;
    private readonly IChannelRegistry _channels;
    private readonly IAgentCapacityService _capacity;
    private readonly IWhatsAppSessionWindow _whatsAppWindow;
    private readonly ILicensedAgentOwnershipWriter _ownershipWriter;
    private readonly IClock _clock;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<OutboundConversationStarter> _logger;

    public OutboundConversationStarter(
        IConversationStore conversations,
        IContactStore contacts,
        IMessageStore messages,
        IChannelRegistry channels,
        IAgentCapacityService capacity,
        IWhatsAppSessionWindow whatsAppWindow,
        ILicensedAgentOwnershipWriter ownershipWriter,
        IClock clock,
        PlatformEventBus eventBus,
        ILogger<OutboundConversationStarter> logger)
    {
        _conversations = conversations;
        _contacts = contacts;
        _messages = messages;
        _channels = channels;
        _capacity = capacity;
        _whatsAppWindow = whatsAppWindow;
        _ownershipWriter = ownershipWriter;
        _clock = clock;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<OutboundStartOutcome> StartAsync(OutboundStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = request.TenantId;

        // Pre-fix GetOrCreateForContactAsync: a new conversation is persisted Queued and unowned before any send.
        var conversation = await _conversations.FindActiveByContactAsync(tenantId, request.ContactId, request.Channel, ct)
            .ConfigureAwait(false);
        var reused = conversation is not null;
        if (conversation is null)
        {
            conversation = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = tenantId,
                ContactId = request.ContactId,
                Channel = request.Channel,
                State = ConversationState.Queued,
                CreatedAt = _clock.UtcNow,
            };
            await _conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
        }

        // Pre-fix SendMessageAsync: the agent must already own the conversation.
        if (conversation.Owner is not { Kind: ConversationOwnerKind.Agent } owner || owner.OwnerId != request.AgentId)
            throw new InvalidOperationException(
                $"Agent '{request.AgentId}' is not the owner of conversation '{conversation.ConversationId}'.");

        var refused = false;
        if (conversation.Channel == ChannelType.WhatsApp)
        {
            var window = await _whatsAppWindow.DecideAsync(tenantId, conversation.ConversationId, request.TemplateName, ct)
                .ConfigureAwait(false);
            refused = window.IsRefused;
        }

        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = conversation.Channel,
            SenderId = request.AgentId.Value,
            Content = request.Content,
            DeliveryStatus = refused ? MessageDeliveryStatus.Failed : MessageDeliveryStatus.Pending,
            CreatedAt = _clock.UtcNow,
        };
        await _messages.SaveAsync(message, ct).ConfigureAwait(false);

        if (!refused)
        {
            var contact = await _contacts.GetByIdAsync(tenantId, conversation.ContactId, ct).ConfigureAwait(false);
            if (contact?.FindAddress(conversation.Channel) is { } address)
            {
                var result = await _channels.GetConnector(conversation.Channel).SendAsync(
                    new OutboundMessage(address, request.Content, tenantId, conversation.ConversationId, request.TemplateName),
                    ct).ConfigureAwait(false);
                if (result.Success)
                {
                    await _messages.MarkSentAsync(tenantId, message.MessageId, result.ExternalMessageId, ct).ConfigureAwait(false);
                    message.DeliveryStatus = MessageDeliveryStatus.Sent;
                }
                else
                {
                    await _messages.UpdateDeliveryStatusAsync(
                        tenantId, message.MessageId, MessageDeliveryStatus.Failed, null, ct).ConfigureAwait(false);
                    message.DeliveryStatus = MessageDeliveryStatus.Failed;
                    LogSendFailed(_logger, conversation.ConversationId.Value, result.ErrorCode);
                }
            }
        }

        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversation.ConversationId.Value, message.MessageId.Value,
            "Outbound", conversation.Channel.ToString()));

        return OutboundStartOutcome.Started(conversation, message, reused);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel send failed for conversation {ConversationId}: [{ErrorCode}]")]
    private static partial void LogSendFailed(ILogger logger, string conversationId, string? errorCode);
}
