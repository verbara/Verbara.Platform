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
/// A failed send stores nothing — no conversation, message, capacity or ledger row. On WebChat a new conversation goes to
/// the contact's session that is connected now, newest first, and records that session in its insert, so its replies and
/// the visitor's answer stay with that session; a reused one goes to the session it records.
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

    /// <summary>
    /// The most contacts the address side of a start's refusal reads, as the dial's. Restricted contacts sort first, so a
    /// shorter list never drops one that changes the answer.
    /// </summary>
    internal const int AddressMatchLimit = 50;

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

        // A new conversation is not started with a contact marked do-not-contact, nor on a channel whose consent the
        // contact recorded as no, and that holds for every contact of the tenant holding the same address on the channel:
        // a duplicate contact's restriction is not bypassed through its unmarked twin. The caller's own open, sendable
        // conversation is still reused: a message on that thread is accepted the same way when it is sent to the
        // conversation directly. A channel with no recorded answer refuses nothing.
        if (open is null && await ContactRestrictionAsync(tenantId, contact, address, channel, ct).ConfigureAwait(false) is { } restriction)
            return Refuse(request, restriction);

        if (channel == ChannelType.WhatsApp)
        {
            // Meta's customer-service window belongs to the user: any inbound message from this contact on WhatsApp
            // in the last 24 hours, in any of its conversations with the tenant, opens it.
            var lastInboundAt = await _conversations.FindLastInboundAtAsync(tenantId, request.ContactId, channel, ct)
                .ConfigureAwait(false);
            if (WhatsAppSessionWindow.Decide(lastInboundAt, _clock.UtcNow, request.TemplateName).IsRefused)
                return Refuse(request, OutboundStartCodes.WhatsAppTemplateRequired);
        }

        // The send claim of a reuse (conversations-close-after-inactivity, design D9): after every refusal and before
        // the provider send, which cannot be undone. It confirms the row is still sendable and advances its clock; an
        // inactivity close that committed after the IsSendable check above refuses the start as that check does, and
        // nothing is sent or stored. A send that then fails keeps the clock advanced (harmless: one more window).
        if (open is not null
            && !await _conversations.TryClaimForSendAsync(tenantId, open.ConversationId, _clock.UtcNow, advanceClock: true, ct)
                .ConfigureAwait(false))
        {
            LogRefused(_logger, tenantId.Value, request.ContactId.Value, channel,
                OutboundStartCodes.ConversationNotSendable);
            return OutboundStartOutcome.NamingOpen(
                OutboundStartCodes.ConversationNotSendable, open.ConversationId, ConversationOwnerKind.Agent);
        }

        var destination = await DestinationAsync(contact, open, address, channel, ct).ConfigureAwait(false);

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
            sent = await SendAsync(request, destination, conversationId, ct).ConfigureAwait(false);
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
        var conversation = open ?? BornActive(request, conversationId, destination, now);

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

        // The live-stream audience (contact-history-opens-by-assignment, design D17): the conversation the message went
        // out in, reused or born here, is owned by the starting agent.
        _eventBus.Publish(new ConversationMessageEvent(
            tenantId.Value, conversationId.Value, message.MessageId.Value, "Outbound", channel.ToString())
        {
            Audience = ConversationOffer.AudienceOf(conversation),
        });

        return OutboundStartOutcome.Started(conversation, message, reused: open is not null);
    }

    /// <summary>
    /// <see cref="OutboundStartCodes.ContactDoNotContact"/> when the named contact or any contact of the tenant holding
    /// <paramref name="address"/> on <paramref name="channel"/> (<see cref="IContactStore.FindAllByChannelAddressAsync"/>,
    /// restricted first) is marked do-not-contact, else <see cref="OutboundStartCodes.ContactConsentWithdrawn"/> when any
    /// records its consent on the channel as <c>false</c>, else <c>null</c>. The address is the destination of every
    /// channel but WebChat, whose destination is the contact's own key or one of its own session addresses. Store errors
    /// are not caught.
    /// </summary>
    private async Task<string?> ContactRestrictionAsync(
        TenantId tenantId, Contact named, ChannelAddress address, ChannelType channel, CancellationToken ct)
    {
        var holders = await _contacts.FindAllByChannelAddressAsync(tenantId, channel, address.Address, AddressMatchLimit, ct)
            .ConfigureAwait(false);

        var consentWithdrawn = false;
        foreach (var contact in holders.Prepend(named))
        {
            if (contact.DoNotContact)
                return OutboundStartCodes.ContactDoNotContact;
            if (contact.ChannelConsent.TryGetValue(channel, out var consented) && !consented)
                consentWithdrawn = true;
        }

        return consentWithdrawn ? OutboundStartCodes.ContactConsentWithdrawn : null;
    }

    // A contact can hold several WebChat sessions. No connected session keeps the contact's own address, whose send is
    // then refused as it always was; every other channel has one destination.
    private async Task<ChannelAddress> DestinationAsync(
        Contact contact, Conversation? open, ChannelAddress contactAddress, ChannelType channel, CancellationToken ct)
    {
        if (channel != ChannelType.WebChat)
            return contactAddress;

        if (open is not null)
            return WebChatConversationMetadata.SessionAddressOf(open) ?? contactAddress;

        return _channels.GetConnector(channel) is IConnectedSessionDirectory directory
            ? await directory.FindNewestConnectedAddressAsync(contact, ct).ConfigureAwait(false) ?? contactAddress
            : contactAddress;
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

    private static Conversation BornActive(
        OutboundStartRequest request, EntityId conversationId, ChannelAddress destination, DateTimeOffset now)
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
            // Born with its first message: the store's INSERT keeps it (COALESCE(@LastMessageAt, @CreatedAt), design D2).
            LastMessageAt = now,
            CreatedBy = request.ActorUserId,
            UpdatedBy = request.ActorUserId,
        };
        if (request.Channel == ChannelType.WebChat)
            conversation.SetMetadata(WebChatConversationMetadata.SessionAddressKey, destination.Address);
        return conversation;
    }

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
