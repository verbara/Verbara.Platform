using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 2.3: the real inbound pipeline, <see cref="WebhookInboundProcessor"/> and
/// <see cref="WebChatInboundRouter"/> over the real in-memory stores, reached through decorators that inject the
/// delay points the race tests need (after the open-conversation read, around the return close, before and after the
/// message insert). Only the router and the switchboard are substitutes; the switchboard's queue assignment gives the
/// row its queue owner, as the real one does.
/// </summary>
internal sealed class CustomerReturnHarness : IDisposable
{
    public static readonly EntityId QueueId = EntityId.From("q-return");
    public static readonly EntityId AgentA = EntityId.From("agent-a");

    private readonly List<ConversationStateChangedEvent> _stateChanges = [];
    private readonly IDisposable _subscription;
    private readonly MeterListener _listener = new();
    private long _customerReturnedCount;

    public CustomerReturnHarness(ChannelType channel = ChannelType.WhatsApp)
    {
        Channel = channel;
        Clock.UtcNow.Returns(_ => Now);
        Inner = new InMemoryConversationStore(Messages);
        Store = new HookedConversationStore(Inner);
        MessageStore = new HookedMessageStore(Messages);
        Contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = Now.AddDays(-30) };
        Contact.AddAddress(Address);
        Contacts.GetByIdAsync(Tenant, Contact.ContactId, Arg.Any<CancellationToken>()).Returns(Contact);
        Lifecycle = new DefaultConversationLifecycleService(Store, Clock);
        Pipeline = new InboundMessagePipeline(MessageStore, new FixedContactResolver(Contact), Store, Lifecycle, Clock);

        Router.RouteAsync(default!, default).ReturnsForAnyArgs(new RouteResult(QueueId, default, null, null));
        Switchboard.When(s => s.AssignToQueueAsync(Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                var row = Inner.GetByIdAsync(ci.ArgAt<TenantId>(1), ci.ArgAt<EntityId>(0), CancellationToken.None).GetAwaiter().GetResult();
                if (row is not null)
                    row.Owner = ConversationOwner.ForQueue(ci.ArgAt<EntityId>(2));
            });

        Announcer = new CustomerReturnAnnouncer(EventBus, Audit, Metrics, AnnouncerLogger);
        _subscription = EventBus.Events.Subscribe(e =>
        {
            if (e is ConversationStateChangedEvent sc)
                lock (_stateChanges) _stateChanges.Add(sc);
        });
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, Metrics.SystemClosed))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "reason", Value: ConversationCloseReasons.CustomerReturned })
                    Interlocked.Add(ref _customerReturnedCount, value);
            }
        });
        _listener.Start();
    }

    public TenantId Tenant { get; } = new("t-customer-return");
    public ChannelType Channel { get; }
    public ChannelAddress Address => new(Channel, Channel == ChannelType.WebChat ? "webchat-return" : "+573001112233");
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    public IClock Clock { get; } = Substitute.For<IClock>();
    public InMemoryMessageStore Messages { get; } = new();
    public InMemoryConversationStore Inner { get; }
    public HookedConversationStore Store { get; }
    public HookedMessageStore MessageStore { get; }
    public Contact Contact { get; }
    public IContactStore Contacts { get; } = Substitute.For<IContactStore>();
    public DefaultConversationLifecycleService Lifecycle { get; }
    public InboundMessagePipeline Pipeline { get; }
    public IInboundRouter Router { get; } = Substitute.For<IInboundRouter>();
    public IConversationSwitchboard Switchboard { get; } = Substitute.For<IConversationSwitchboard>();
    public PlatformEventBus EventBus { get; } = new();
    public IAuditService Audit { get; } = Substitute.For<IAuditService>();
    public ConversationCloseMetrics Metrics { get; } = new();
    public CapturingLogger<CustomerReturnAnnouncer> AnnouncerLogger { get; } = new();
    public CapturingLogger<WebhookInboundProcessor> ProcessorLogger { get; } = new();
    public CustomerReturnAnnouncer Announcer { get; }

    public IReadOnlyList<ConversationStateChangedEvent> ReturnCloseEvents
    {
        get
        {
            lock (_stateChanges)
                return [.. _stateChanges.Where(e => e is { OldState: nameof(ConversationState.Resolved), NewState: nameof(ConversationState.Closed) })];
        }
    }

    public int ReturnAuditCount => Audit.ReceivedCalls().Count(c =>
        c.GetMethodInfo().Name == nameof(IAuditService.RecordAsync)
        && c.GetArguments() is { Length: > 5 } args
        && args[2] is "conversation.closed"
        && args[4] is CustomerReturnAnnouncer.ActorId
        && args[5] is "system"
        && args[10] is IReadOnlyDictionary<string, string> meta
        && meta.TryGetValue("reason", out var reason) && reason == ConversationCloseReasons.CustomerReturned);

    public long CustomerReturnedCount
    {
        get
        {
            _listener.RecordObservableInstruments();
            return Interlocked.Read(ref _customerReturnedCount);
        }
    }

    public int AssignmentsOf(EntityId conversationId) => Switchboard.ReceivedCalls().Count(c =>
        c.GetMethodInfo().Name == nameof(IConversationSwitchboard.AssignToQueueAsync)
        && c.GetArguments()[0] is EntityId id && id == conversationId);

    public WebhookInboundProcessor CreateProcessor(IInboundMessagePipeline? pipeline = null)
    {
        var statuses = new DeliveryStatusHandler(Messages, NullLogger<DeliveryStatusHandler>.Instance);
        return new WebhookInboundProcessor(
            pipeline ?? Pipeline, Router, Switchboard, Substitute.For<IConversationService>(), Store, Contacts,
            Substitute.For<IVirtualAgent>(), Lifecycle, statuses, EventBus, Announcer, ProcessorLogger);
    }

    public WebChatSessionManager Sessions { get; } = new(Options.Create(new WebChatOptions()));

    public WebChatInboundRouter CreateWebChatRouter() => new(
        Router, Switchboard, Store, Contacts, EventBus, Sessions, Announcer, NullLogger<WebChatInboundRouter>.Instance);

    public async Task<string> ConnectSessionAsync(EntityId conversationId) =>
        await Sessions.ConnectAsync(Tenant, Contact.ContactId, conversationId, Address, Clock);

    public async Task<Conversation> SeedAsync(
        ConversationState state, TimeSpan idle, ConversationOwner? owner = null, bool ownerless = false)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = Contact.ContactId,
            Channel = Channel,
            State = state,
            Owner = ownerless ? null : owner ?? ConversationOwner.ForAgent(AgentA),
            CreatedAt = Now - idle - TimeSpan.FromDays(1),
            UpdatedAt = Now - idle,
            LastMessageAt = Now - idle,
        };
        await Inner.SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    public InboundMessage Inbound(string externalId, DateTimeOffset? providerTimestamp = null, string text = "hola") =>
        new(Address, new MessageEnvelope([new TextBlock(text)]), externalId, providerTimestamp ?? Now);

    public async Task<IReadOnlyList<Message>> MessagesOfAsync(EntityId conversationId) =>
        await Messages.GetConversationMessagesAsync(Tenant, conversationId, 100, 0, CancellationToken.None);

    public async Task<IReadOnlyList<Conversation>> ConversationsAsync() =>
        (await Inner.ListAsync(Tenant, new ConversationQuery { PageSize = 100 }, CancellationToken.None)).Items;

    public async Task<IReadOnlyList<Conversation>> ConversationsHoldingAsync(string externalId)
    {
        var checkedConversations = new List<(Conversation Conversation, bool Holds)>();
        foreach (var conversation in await ConversationsAsync())
            checkedConversations.Add((conversation, (await MessagesOfAsync(conversation.ConversationId)).Any(m => m.ExternalMessageId == externalId)));

        return checkedConversations.Where(c => c.Holds).Select(c => c.Conversation).ToList();
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _listener.Dispose();
        Metrics.Dispose();
        EventBus.Dispose();
    }

    private sealed class FixedContactResolver(Contact contact) : IContactIdentityResolver
    {
        public Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) => Task.FromResult(contact);
    }
}

/// <summary>Records every log entry with its level, event id and exception.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries
    {
        get { lock (_entries) return [.. _entries]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}

/// <summary>
/// Forwards every member to the real store. The hooks are the injected delay points (ADR-0030 widened windows): they
/// run inside the call, so a test can hold one delivery at an exact point of the pipeline.
/// </summary>
internal sealed class HookedConversationStore(IConversationStore inner) : IConversationStore
{
    private int _returnCloseWins;

    // The in-memory store hands every caller the same Conversation instance, so the unconditional close below (the
    // control's lost-update stand-in) must not mutate it from two deliveries at once: its metadata Dictionary is not
    // thread-safe (PR #533 CI, 667238f8: "Operations that change non-concurrent collections must have exclusive
    // access" in ProcessAsync_ShouldDoubleClose_WhenTheReturnCloseIsUnconditional_Control). Both deliveries still win.
    private readonly Lock _unconditionalCloseLock = new();

    /// <summary>Runs after <c>FindActiveByContactAsync</c> read a row (or none), before the caller sees it.</summary>
    public Func<Conversation?, Task>? AfterFindActive { get; set; }

    /// <summary>Runs before the inbound claim reaches the store.</summary>
    public Func<EntityId, Task>? BeforeClaim { get; set; }

    /// <summary>Runs before the return close reaches the store.</summary>
    public Func<EntityId, Task>? BeforeCloseResolved { get; set; }

    /// <summary>Runs after the return close, with whether this caller won it.</summary>
    public Func<EntityId, bool, Task>? AfterCloseResolved { get; set; }

    /// <summary>The baseline of the widened-window control: the return close always "wins" (no state condition).</summary>
    public bool UnconditionalReturnClose { get; set; }

    public int ReturnCloseWins => Volatile.Read(ref _returnCloseWins);

    public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        // A copy, as the Postgres store materializes the row: the in-memory store hands out its live entity, which a
        // concurrent close would mutate under the reader's feet (the pipeline decides on the state it READ).
        var found = Snapshot(await inner.FindActiveByContactAsync(tenantId, contactId, channel, ct));
        if (AfterFindActive is { } hook)
            await hook(found);
        return found;
    }

    public async Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        if (BeforeClaim is { } hook)
            await hook(conversationId);
        return await inner.TryClaimForInboundAsync(tenantId, conversationId, now, ct);
    }

    public async Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        if (BeforeCloseResolved is { } before)
            await before(conversationId);

        bool won;
        if (UnconditionalReturnClose)
        {
            var row = await inner.GetByIdAsync(tenantId, conversationId, ct);
            won = row is not null;
            if (row is not null)
            {
                lock (_unconditionalCloseLock)
                {
                    row.State = ConversationState.Closed;
                    row.ClosedAt = now;
                    row.SetMetadata(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.CustomerReturned);
                }

                await inner.SaveAsync(row, ct);
            }
        }
        else
        {
            won = await inner.TryCloseResolvedForReplyAsync(tenantId, conversationId, now, ct);
        }

        if (won)
            Interlocked.Increment(ref _returnCloseWins);
        if (AfterCloseResolved is { } after)
            await after(conversationId, won);
        return won;
    }

    private static Conversation? Snapshot(Conversation? c)
    {
        if (c is null)
            return null;
        var copy = new Conversation
        {
            ConversationId = c.ConversationId,
            TenantId = c.TenantId,
            ContactId = c.ContactId,
            Channel = c.Channel,
            Owner = c.Owner,
            State = c.State,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            ClosedAt = c.ClosedAt,
            LastMessageAt = c.LastMessageAt,
        };
        foreach (var kv in c.Metadata)
            copy.SetMetadata(kv.Key, kv.Value);
        return copy;
    }

    /// <summary>When it returns an exception, the read by id throws it instead of reaching the store.</summary>
    public Func<EntityId, Exception?>? GetByIdFault { get; set; }

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
        GetByIdFault?.Invoke(conversationId) is { } fault
            ? Task.FromException<Conversation?>(fault)
            : inner.GetByIdAsync(tenantId, conversationId, ct);

    public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct) => inner.ListAsync(tenantId, query, ct);
    public Task SaveAsync(Conversation conversation, CancellationToken ct) => inner.SaveAsync(conversation, ct);
    public Task<DateTimeOffset?> FindLastInboundAtAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) => inner.FindLastInboundAtAsync(tenantId, contactId, channel, ct);
    public Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct) => inner.FindByVoiceLinkedIdAsync(tenantId, voiceLinkedId, ct);
    public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct) => inner.FindByVoiceLinkedIdAcrossTenantsAsync(voiceLinkedId, ct);
    public Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct) => inner.FindByIdAcrossTenantsAsync(conversationId, ct);
    public Task<PagedResult<ContactConversationSummary>> ListContactSummariesAsync(
        TenantId tenantId, EntityId contactId, EntityId? ownedByAgentId, int page, int pageSize, CancellationToken ct) =>
        inner.ListContactSummariesAsync(tenantId, contactId, ownedByAgentId, page, pageSize, ct);
    public Task<bool> HoldsOpenConversationOfContactAsync(TenantId tenantId, EntityId contactId, EntityId agentId, CancellationToken ct) =>
        inner.HoldsOpenConversationOfContactAsync(tenantId, contactId, agentId, ct);

    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) => inner.ListByContactAsync(tenantId, contactId, ct);
    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) => inner.DeleteByContactAsync(tenantId, contactId, ct);
    public Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) => inner.DeleteIfNoMessagesAsync(tenantId, conversationId, ct);
    public Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct) => inner.DeleteClosedBeforeAsync(tenantId, cutoff, ct);
    public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct) => inner.ListQueuedAsync(tenantId, limit, ct);
    public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct) => inner.ListByStateAsync(tenantId, state, limit, ct);
    public Task<bool> TryConditionalCloseWrapUpAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) => inner.TryConditionalCloseWrapUpAsync(tenantId, conversationId, now, ct);
    public Task<bool> TryCommitWrapUpAsync(TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct) => inner.TryCommitWrapUpAsync(tenantId, conversationId, ownerAgentId, now, ct);
    public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) => inner.CountActiveWorkAsync(tenantId, agentId, ct);
    public Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) => inner.ListFailoverWorkByOwnerAsync(tenantId, agentId, ct);
    public Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct) => inner.ListPendingCallbackEvalAsync(ct);
    public Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct) => inner.ListCallbackStuckAsync(tenantId, ct);
    public Task<IReadOnlyList<Conversation>> ListInactiveAsync(TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct) => inner.ListInactiveAsync(tenantId, states, cutoff, limit, ct);
    public Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct) => inner.TryCloseInactiveAsync(tenantId, request, ct);
    public Task<bool> TryClaimForSendAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct) => inner.TryClaimForSendAsync(tenantId, conversationId, now, advanceClock, ct);
    public Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct) => inner.ReconcileLastMessageAtAsync(tenantId, ct);
}

/// <summary>Forwards every member to the real message store; hooks around the inbound insert.</summary>
internal sealed class HookedMessageStore(IMessageStore inner) : IMessageStore
{
    /// <summary>Runs before the inbound insert reaches the store (after conversation resolution).</summary>
    public Func<Message, Task>? BeforeInsert { get; set; }

    /// <summary>Runs after the inbound insert, with what the store returned.</summary>
    public Func<Message, Message, Task>? AfterInsert { get; set; }

    /// <summary>When it returns an exception, the insert throws it instead of reaching the store.</summary>
    public Func<Message, Exception?>? InsertFault { get; set; }

    public async Task<Message> InsertInboundIfAbsentAsync(Message message, CancellationToken ct)
    {
        if (BeforeInsert is { } before)
            await before(message);
        if (InsertFault?.Invoke(message) is { } fault)
            throw fault;
        var stored = await inner.InsertInboundIfAbsentAsync(message, ct);
        if (AfterInsert is { } after)
            await after(message, stored);
        return stored;
    }

    public Task SaveAsync(Message message, CancellationToken ct) => inner.SaveAsync(message, ct);
    public Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct) => inner.GetByIdAsync(tenantId, messageId, ct);
    public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct) => inner.GetConversationMessagesAsync(tenantId, conversationId, limit, offset, ct);
    public Task<MessageHistorySlice> GetConversationHistoryAsync(TenantId tenantId, EntityId conversationId, int limit, MessageHistoryCursor? before, CancellationToken ct) => inner.GetConversationHistoryAsync(tenantId, conversationId, limit, before, ct);
    public Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct) => inner.UpdateDeliveryStatusAsync(tenantId, messageId, status, timestamp, ct);
    public Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct) => inner.FindByExternalIdAsync(tenantId, externalMessageId, ct);
    public Task MarkSentAsync(TenantId tenantId, EntityId messageId, string? externalMessageId, CancellationToken ct) => inner.MarkSentAsync(tenantId, messageId, externalMessageId, ct);
    /// <summary>
    /// Runs before <c>FindLastInboundAsync</c> (the WhatsApp window read, which a send makes after its state check and
    /// before it stores anything): task 2.4's point for the inactivity close to commit inside a send.
    /// </summary>
    public Func<EntityId, Task>? BeforeFindLastInbound { get; set; }

    public async Task<Message?> FindLastInboundAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        if (BeforeFindLastInbound is { } hook)
            await hook(conversationId);
        return await inner.FindLastInboundAsync(tenantId, conversationId, ct);
    }

    public Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct) => inner.GetByConversationIdsAsync(tenantId, conversationIds, ct);
    public Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct) => inner.DeleteByConversationIdsAsync(tenantId, conversationIds, ct);
    public Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct) => inner.DeleteOrphanedAsync(tenantId, ct);
}
