using Microsoft.Extensions.Logging;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// Starts an outbound digital conversation in the calling agent's own name (agent-started-conversations,
/// design D14). Every refusal is decided before the provider is reached; the provider send then happens
/// <b>first</b>, and only a successful send persists anything: a new conversation is born
/// <see cref="ConversationState.Active"/>, owned by the agent, committed through metering's
/// <see cref="ILicensedAgentOwnershipWriter"/>, and its message is stored <see cref="MessageDeliveryStatus.Sent"/>
/// with the provider's id. A failed send stores nothing — no conversation, message, capacity or ledger row.
/// </summary>
internal sealed partial class OutboundConversationStarter : IOutboundConversationStarter
{
    /// <summary>
    /// The existing ownership-change kind an own-name start is committed under. D14 adds no
    /// <see cref="OwnershipChangeKind"/> and no ledger kind; of the three that exist, a takeover is the one
    /// whose receiver is the actor's own agent, which is exactly an own-name start. Starting on behalf of
    /// another agent is out of scope (it would be a reassign through <c>TransferToAgentAsync</c>).
    /// </summary>
    internal const OwnershipChangeKind OwnNameStartKind = OwnershipChangeKind.TakenOver;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorUserId);
        var tenantId = request.TenantId;
        var channel = request.Channel;

        // ── Refusals: all decided before anything is sent or stored ─────────────────────────────────
        var contact = await _contacts.GetByIdAsync(tenantId, request.ContactId, ct).ConfigureAwait(false);
        if (contact?.FindAddress(channel) is not { } address)
            return Refuse(request, OutboundStartCodes.ContactHasNoAddress);

        if (!_channels.AvailableChannels.Contains(channel))
            return Refuse(request, OutboundStartCodes.ChannelNotAvailable);

        var open = await _conversations.FindActiveByContactAsync(tenantId, request.ContactId, channel, ct)
            .ConfigureAwait(false);
        if (open is not null && !OwnedBy(open, request.AgentId))
        {
            // Owned by another agent, a queue or a bot, or queued unowned: never taken over (DQ5).
            LogRefused(_logger, tenantId.Value, request.ContactId.Value, channel,
                OutboundStartCodes.ConversationOpenElsewhere);
            return OutboundStartOutcome.OpenElsewhere(open.ConversationId, open.Owner?.Kind);
        }

        if (open is not null)
            EnsureSendable(open);
        else if (!await _capacity.HasCapacityAsync(tenantId, request.AgentId, channel, ct).ConfigureAwait(false))
            return Refuse(request, OutboundStartCodes.AgentAtCapacity);

        var conversationId = open?.ConversationId ?? EntityId.New();
        if (channel == ChannelType.WhatsApp)
        {
            // A brand-new conversation has no inbound history, so its window is closed: it needs a template.
            var window = await _whatsAppWindow.DecideAsync(tenantId, conversationId, request.TemplateName, ct)
                .ConfigureAwait(false);
            if (window.IsRefused)
                return Refuse(request, OutboundStartCodes.WhatsAppTemplateRequired);
        }

        // ── Send first: nothing is persisted unless the provider accepted the message ───────────────
        var sent = await SendAsync(request, address, conversationId, ct).ConfigureAwait(false);
        if (sent is null)
            return OutboundStartOutcome.Refused(OutboundStartCodes.ChannelSendFailed);

        var now = _clock.UtcNow;
        var conversation = open ?? await CommitNewAsync(request, conversationId, now, ct).ConfigureAwait(false);

        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversation.ConversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = channel,
            SenderId = request.AgentId.Value,
            Content = request.Content,
            DeliveryStatus = MessageDeliveryStatus.Sent,
            ExternalMessageId = sent.ExternalMessageId,
            CreatedAt = now,
            CreatedBy = request.ActorUserId,
        };
        await _messages.SaveAsync(message, ct).ConfigureAwait(false);

        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversation.ConversationId.Value, message.MessageId.Value,
            "Outbound", channel.ToString()));

        return OutboundStartOutcome.Started(conversation, message, reused: open is not null);
    }

    // The provider send. A rejection or a connector fault returns null: the caller persists nothing.
    private async Task<SendResult?> SendAsync(
        OutboundStartRequest request, ChannelAddress address, EntityId conversationId, CancellationToken ct)
    {
        var outbound = new OutboundMessage(
            address, request.Content, request.TenantId, conversationId, request.TemplateName, request.TemplateLanguage);
        try
        {
            var result = await _channels.GetConnector(request.Channel).SendAsync(outbound, ct).ConfigureAwait(false);
            if (result.Success)
                return result;

            LogSendFailed(_logger, request.TenantId.Value, request.Channel, result.ErrorCode);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogConnectorFault(_logger, ex, request.TenantId.Value, request.Channel);
            return null;
        }
    }

    // A new conversation, born Active and owned by the agent, reserves one unit of its capacity and is saved
    // only through metering's ownership writer. Should the commit fail after the provider accepted the message,
    // the reservation is released so the agent's load stays true, and the failure propagates.
    private async Task<Conversation> CommitNewAsync(
        OutboundStartRequest request, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var conversation = new Conversation
        {
            ConversationId = conversationId,
            TenantId = request.TenantId,
            ContactId = request.ContactId,
            Channel = request.Channel,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(request.AgentId),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = request.ActorUserId,
            UpdatedBy = request.ActorUserId,
        };

        await _capacity.ReserveAsync(request.TenantId, request.AgentId, request.Channel, ct).ConfigureAwait(false);
        try
        {
            await _ownershipWriter.CommitOwnershipAsync(
                conversation, new OwnershipChange(OwnNameStartKind, request.ActorUserId), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCommitFailedAfterSend(_logger, ex, request.TenantId.Value, conversationId.Value);
            await _capacity.ReleaseAsync(request.TenantId, request.AgentId, request.Channel, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        _eventBus.Publish(new ConversationAssignedEvent(
            request.TenantId.Value, conversationId.Value, request.AgentId.Value, "", "", ""));
        _eventBus.Publish(new ConversationStateChangedEvent(
            request.TenantId.Value, conversationId.Value, "", nameof(ConversationState.Active)));
        return conversation;
    }

    private static bool OwnedBy(Conversation conversation, EntityId agentId) =>
        conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId } && ownerId == agentId;

    // The caller's own open conversation is reused only in a state a message can be sent in.
    private static void EnsureSendable(Conversation conversation)
    {
        if (conversation.State is not (ConversationState.Active or ConversationState.OnHold or ConversationState.Consulting))
            throw new InvalidOperationException(
                $"Cannot send message in conversation state '{conversation.State}'.");
    }

    private OutboundStartOutcome Refuse(OutboundStartRequest request, string code)
    {
        LogRefused(_logger, request.TenantId.Value, request.ContactId.Value, request.Channel, code);
        return OutboundStartOutcome.Refused(code);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Agent start refused for tenant {TenantId}, contact {ContactId} on {Channel}: {Code}; nothing was sent or stored.")]
    private static partial void LogRefused(ILogger logger, string tenantId, string contactId, ChannelType channel, string code);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent start: the {Channel} provider rejected the first message for tenant {TenantId} [{ErrorCode}]; nothing was stored.")]
    private static partial void LogSendFailed(ILogger logger, string tenantId, ChannelType channel, string? errorCode);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent start: the {Channel} connector failed for tenant {TenantId}; nothing was stored.")]
    private static partial void LogConnectorFault(ILogger logger, Exception exception, string tenantId, ChannelType channel);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Agent start: the first message of conversation {ConversationId} (tenant {TenantId}) was sent, but the ownership commit failed; the conversation was not stored.")]
    private static partial void LogCommitFailedAfterSend(ILogger logger, Exception exception, string tenantId, string conversationId);
}
