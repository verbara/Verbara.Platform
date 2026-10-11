using Microsoft.Extensions.Logging;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// Starts an outbound digital conversation in the calling agent's own name (agent-started-conversations,
/// design D14). Every refusal is decided before the provider is reached; the provider send then happens
/// <b>first</b>, and only a successful send persists anything: the message is stored
/// <see cref="MessageDeliveryStatus.Sent"/> with the provider's id, and a new conversation is born
/// <see cref="ConversationState.Active"/>, owned by the agent, and saved through the conversation store.
/// A failed send stores nothing — no conversation, message, capacity or ledger row.
/// </summary>
/// <remarks>
/// <para>
/// <b>No licensed-agent ledger row.</b> An agent starting a conversation in its own name does not change the set of
/// counted agents and is not an ownership move, so the start does not go through metering's ownership writer and
/// appends nothing to <c>license_agent_events</c>; the <c>conversation.started</c> audit records it.
/// </para>
/// <para>
/// <b>Concurrency.</b> Starts for the same tenant, contact and channel run one at a time in this process, and the
/// agent's capacity is taken with an atomic try-reserve before the send, so a double submit cannot open two
/// conversations or overrun the agent's capacity. Across replicas neither is serialised (a known limitation,
/// recorded in design D14).
/// </para>
/// </remarks>
internal sealed partial class OutboundConversationStarter : IOutboundConversationStarter
{
    private readonly IConversationStore _conversations;
    private readonly IContactStore _contacts;
    private readonly IMessageStore _messages;
    private readonly IChannelRegistry _channels;
    private readonly IAgentCapacityService _capacity;
    private readonly IClock _clock;
    private readonly PlatformEventBus _eventBus;
    private readonly ILogger<OutboundConversationStarter> _logger;
    private readonly KeyedGate<(TenantId, EntityId, ChannelType)> _gate = new();

    public OutboundConversationStarter(
        IConversationStore conversations,
        IContactStore contacts,
        IMessageStore messages,
        IChannelRegistry channels,
        IAgentCapacityService capacity,
        IClock clock,
        PlatformEventBus eventBus,
        ILogger<OutboundConversationStarter> logger)
    {
        _conversations = conversations;
        _contacts = contacts;
        _messages = messages;
        _channels = channels;
        _capacity = capacity;
        _clock = clock;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<OutboundStartOutcome> StartAsync(OutboundStartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorUserId);

        using (await _gate.EnterAsync((request.TenantId, request.ContactId, request.Channel), ct).ConfigureAwait(false))
            return await StartGatedAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<OutboundStartOutcome> StartGatedAsync(OutboundStartRequest request, CancellationToken ct)
    {
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
            return OutboundStartOutcome.NamingOpen(
                OutboundStartCodes.ConversationOpenElsewhere, open.ConversationId, open.Owner?.Kind);
        }

        if (open is not null && !IsSendable(open.State))
        {
            // The caller's own conversation, but wrapping up, snoozed or waiting: it must resume it first.
            LogRefused(_logger, tenantId.Value, request.ContactId.Value, channel,
                OutboundStartCodes.ConversationNotSendable);
            return OutboundStartOutcome.NamingOpen(
                OutboundStartCodes.ConversationNotSendable, open.ConversationId, ConversationOwnerKind.Agent);
        }

        if (channel == ChannelType.WhatsApp)
        {
            // Meta's customer-service window belongs to the user: any inbound message from this contact on WhatsApp
            // in the last 24 hours, in any of its conversations with the tenant, opens it.
            var lastInboundAt = await _conversations.FindLastInboundAtAsync(tenantId, request.ContactId, channel, ct)
                .ConfigureAwait(false);
            if (WhatsAppSessionWindow.Decide(lastInboundAt, _clock.UtcNow, request.TemplateName).IsRefused)
                return Refuse(request, OutboundStartCodes.WhatsAppTemplateRequired);
        }

        // A new conversation takes one unit of the agent's capacity before the send, atomically; it is given back
        // if the start does not end with a stored conversation. A reuse adds no load.
        var reserved = false;
        if (open is null)
        {
            if (!await _capacity.TryReserveAsync(tenantId, request.AgentId, channel, ct).ConfigureAwait(false))
                return Refuse(request, OutboundStartCodes.AgentAtCapacity);
            reserved = true;
        }

        var conversationId = open?.ConversationId ?? EntityId.New();
        SendResult? sent;
        try
        {
            // ── Send first: nothing is persisted unless the provider accepted the message ───────────
            sent = await SendAsync(request, address, conversationId, ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled while the provider call was in flight: give the unit back and let the abort propagate.
            await ReleaseIfReservedAsync(request, reserved).ConfigureAwait(false);
            throw;
        }

        if (sent is null)
        {
            await ReleaseIfReservedAsync(request, reserved).ConfigureAwait(false);
            return OutboundStartOutcome.Refused(OutboundStartCodes.ChannelSendFailed);
        }

        // ── The provider accepted the message: that cannot be undone, so persistence ignores the request's
        // cancellation (a client abort must not cut it in half).
        var now = _clock.UtcNow;
        var message = new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Outbound,
            Channel = channel,
            SenderId = request.AgentId.Value,
            Content = StoredContent(request),
            DeliveryStatus = MessageDeliveryStatus.Sent,
            ExternalMessageId = sent.ExternalMessageId,
            CreatedAt = now,
            CreatedBy = request.ActorUserId,
        };
        var conversation = open ?? BornActive(request, conversationId, now);

        try
        {
            // The message row first, right after the send: the provider's sent/delivered callback looks it up by
            // provider id and can arrive at once (design D8). The conversation follows; a conversation therefore
            // never exists without its first message. The reverse can happen: if the conversation write fails, the
            // message row stays orphaned (next to its provider id) until retention removes it.
            await _messages.SaveAsync(message, CancellationToken.None).ConfigureAwait(false);
            if (open is null)
                await _conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPersistFailedAfterSend(_logger, ex, tenantId.Value, conversationId.Value, channel, sent.ExternalMessageId);
            await ReleaseIfReservedAsync(request, reserved).ConfigureAwait(false);
            return OutboundStartOutcome.Refused(OutboundStartCodes.StartPersistFailed);
        }

        if (open is null)
        {
            _eventBus.Publish(new ConversationAssignedEvent(
                tenantId.Value, conversationId.Value, request.AgentId.Value, "", "", ""));
            _eventBus.Publish(new ConversationStateChangedEvent(
                tenantId.Value, conversationId.Value, "", nameof(ConversationState.Active)));
        }

        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversationId.Value, message.MessageId.Value, "Outbound", channel.ToString()));

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

    private async Task ReleaseIfReservedAsync(OutboundStartRequest request, bool reserved)
    {
        if (!reserved)
            return;

        try
        {
            await _capacity.ReleaseAsync(request.TenantId, request.AgentId, request.Channel, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never mask the start's own outcome; the restart reconcile recounts the agent's load from conversations.
            LogReleaseFailed(_logger, ex, request.TenantId.Value, request.AgentId.Value);
        }
    }

    private static Conversation BornActive(OutboundStartRequest request, EntityId conversationId, DateTimeOffset now) => new()
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

    // A template goes out instead of the free text (the connector sends only its name and language), so the stored
    // message names the template: the transcript shows what the contact received.
    private static MessageEnvelope StoredContent(OutboundStartRequest request) => request.TemplateName is { } template
        ? new MessageEnvelope([new TextBlock(request.TemplateLanguage is { } language
            ? $"[template {template} {language}]"
            : $"[template {template}]")])
        : request.Content;

    private static bool OwnedBy(Conversation conversation, EntityId agentId) =>
        conversation.Owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId } && ownerId == agentId;

    // The caller's own open conversation is reused only in a state a message can be sent in (as SendMessageAsync).
    private static bool IsSendable(ConversationState state) =>
        state is ConversationState.Active or ConversationState.OnHold or ConversationState.Consulting;

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
        Message = "Agent start: the first message of conversation {ConversationId} (tenant {TenantId}) reached the {Channel} provider as {ProviderMessageId}, but it could not be stored (a message row may remain without its conversation until retention removes it); reconcile by the provider id.")]
    private static partial void LogPersistFailedAfterSend(
        ILogger logger, Exception exception, string tenantId, string conversationId, ChannelType channel, string? providerMessageId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent start: releasing the capacity unit of agent {AgentId} (tenant {TenantId}) failed.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string tenantId, string agentId);

    /// <summary>One async gate per key, removed when its last holder or waiter leaves.</summary>
    private sealed class KeyedGate<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, Entry> _entries = new();
        private readonly Lock _lock = new();

        public async Task<IDisposable> EnterAsync(TKey key, CancellationToken ct)
        {
            Entry entry;
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out entry!))
                    _entries[key] = entry = new Entry();
                entry.Users++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                Leave(key, entry, release: false);
                throw;
            }

            return new Releaser(this, key, entry);
        }

        private void Leave(TKey key, Entry entry, bool release)
        {
            if (release)
                entry.Semaphore.Release();
            lock (_lock)
            {
                if (--entry.Users == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int Users { get; set; }
        }

        private sealed class Releaser(KeyedGate<TKey> gate, TKey key, Entry entry) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    gate.Leave(key, entry, release: true);
            }
        }
    }
}
