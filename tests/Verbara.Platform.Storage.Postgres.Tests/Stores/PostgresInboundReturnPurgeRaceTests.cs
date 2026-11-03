using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Core.Pipeline;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// conversations-close-after-inactivity task 2.3(c) (design D7, D8; #527's R3.3 race), Live-DB: the retention purge
/// (<c>DeleteClosedBeforeAsync</c> + <c>DeleteOrphanedAsync</c>) runs inside the inbound pipeline's window on a real
/// Postgres built from the real migrations. The customer's reply to an idle <c>Resolved</c> conversation must survive
/// in a new conversation whichever of the purge's <c>DELETE</c> and the return close takes the row first.
/// </summary>
public sealed class PostgresInboundReturnPurgeRaceTests : IClassFixture<MessageCorrelationFixture>
{
    private static readonly TenantId Tenant = new("t-return-purge");
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly MessageCorrelationFixture _fixture;

    public PostgresInboundReturnPurgeRaceTests(MessageCorrelationFixture fixture) => _fixture = fixture;

    /// <summary>The purge's DELETE wins: it runs after the pipeline read the Resolved row and before the return close.</summary>
    [Fact]
    public async Task ProcessAsync_ShouldKeepTheReplyInANewConversation_WhenThePurgeDeletesTheResolvedRowAfterTheRead()
    {
        var (ds, store, messages, contact, resolved) = await SeedIdleResolvedAsync();
        var purged = -1;
        var hooked = new PurgeHookStore(store)
        {
            AfterFindActive = async found =>
            {
                if (found?.ConversationId == resolved && purged < 0)
                    purged = await store.DeleteClosedBeforeAsync(Tenant, DateTimeOffset.UtcNow - Retention, CancellationToken.None);
            },
        };

        var result = await PipelineOver(hooked, messages, contact).ProcessAsync(
            Inbound("wamid.purge-first"), Tenant, ChannelType.WhatsApp, CancellationToken.None);
        await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        purged.Should().Be(1, "the purge deleted the idle Resolved row inside the pipeline's window");
        result.ClosedOnReturnId.Should().BeNull("the row was gone, so this delivery closed nothing");
        await AssertReplyKeptInNewConversationAsync(ds, store, messages, resolved, result);
    }

    /// <summary>The return close wins: the purge runs after it and before the message insert, and keeps the row.</summary>
    [Fact]
    public async Task ProcessAsync_ShouldKeepTheReplyInANewConversation_WhenThePurgeRunsAfterTheReturnClose()
    {
        var (ds, store, messages, contact, resolved) = await SeedIdleResolvedAsync();
        var purged = -1;
        var hooked = new PurgeHookStore(store)
        {
            AfterCloseResolved = async (id, won) =>
            {
                won.Should().BeTrue();
                if (id == resolved)
                    purged = await store.DeleteClosedBeforeAsync(Tenant, DateTimeOffset.UtcNow - Retention, CancellationToken.None);
            },
        };

        var result = await PipelineOver(hooked, messages, contact).ProcessAsync(
            Inbound("wamid.close-first"), Tenant, ChannelType.WhatsApp, CancellationToken.None);
        await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        purged.Should().Be(0, "the close stamped closed_at = now, so the purge's clock keeps the row");
        result.ClosedOnReturnId.Should().Be(resolved);
        var closed = (await store.GetByIdAsync(Tenant, resolved, CancellationToken.None))!;
        closed.State.Should().Be(ConversationState.Closed);
        closed.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.CustomerReturned);
        await AssertReplyKeptInNewConversationAsync(ds, store, messages, resolved, result);
    }

    private static async Task AssertReplyKeptInNewConversationAsync(
        Npgsql.NpgsqlDataSource ds, PostgresConversationStore store, PostgresMessageStore messages, EntityId resolved, PipelineResult result)
    {
        (await MessageCorrelationFixture.ScalarAsync(ds, "SELECT COUNT(*) FROM messages")).Should().Be(1, "the reply is not lost");
        result.ConversationId.Should().NotBe(resolved);
        result.IsNewConversation.Should().BeTrue("the reply opens a new conversation, which the caller routes");
        var holder = await store.GetByIdAsync(Tenant, result.ConversationId, CancellationToken.None);
        holder.Should().NotBeNull();
        holder!.State.Should().Be(ConversationState.Queued);
        (await messages.GetConversationMessagesAsync(Tenant, result.ConversationId, 10, 0, CancellationToken.None))
            .Should().ContainSingle(m => m.Direction == MessageDirection.Inbound);
    }

    private async Task<(Npgsql.NpgsqlDataSource Ds, PostgresConversationStore Store, PostgresMessageStore Messages, Contact Contact, EntityId Resolved)>
        SeedIdleResolvedAsync()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var store = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow.AddDays(-90) };
        var idleSince = DateTimeOffset.UtcNow.AddDays(-60);
        var resolved = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Resolved,
            Owner = ConversationOwner.ForAgent(EntityId.From("agent-purge")),
            CreatedAt = idleSince.AddDays(-1),
            UpdatedAt = idleSince,
            LastMessageAt = idleSince,
        };
        await store.SaveAsync(resolved, CancellationToken.None);
        return (ds, store, messages, contact, resolved.ConversationId);
    }

    private static InboundMessagePipeline PipelineOver(IConversationStore store, PostgresMessageStore messages, Contact contact) =>
        new(messages, new FixedContactResolver(contact), store,
            new DefaultConversationLifecycleService(store, new SystemClock()), new SystemClock());

    private static InboundMessage Inbound(string externalId) => new(
        new ChannelAddress(ChannelType.WhatsApp, "+573001112233"),
        new MessageEnvelope([new TextBlock("volví")]),
        externalId,
        DateTimeOffset.UtcNow);

    private sealed class FixedContactResolver(Contact contact) : IContactIdentityResolver
    {
        public Task<Contact> ResolveAsync(TenantId tenantId, ChannelAddress address, CancellationToken ct) => Task.FromResult(contact);
    }

    /// <summary>
    /// The injected delay points of this race: after the open-conversation read and after the return close. Every other
    /// member forwards to the real Postgres store.
    /// </summary>
    private sealed class PurgeHookStore(IConversationStore inner) : IConversationStore
    {
        public Func<Conversation?, Task>? AfterFindActive { get; init; }

        public Func<EntityId, bool, Task>? AfterCloseResolved { get; init; }

        public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
        {
            var found = await inner.FindActiveByContactAsync(tenantId, contactId, channel, ct);
            if (AfterFindActive is { } hook)
                await hook(found);
            return found;
        }

        public async Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
        {
            var won = await inner.TryCloseResolvedForReplyAsync(tenantId, conversationId, now, ct);
            if (AfterCloseResolved is { } hook)
                await hook(conversationId, won);
            return won;
        }

        public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) => inner.GetByIdAsync(tenantId, conversationId, ct);
        public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct) => inner.ListAsync(tenantId, query, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct) => inner.SaveAsync(conversation, ct);
        public Task<DateTimeOffset?> FindLastInboundAtAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) => inner.FindLastInboundAtAsync(tenantId, contactId, channel, ct);
        public Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct) => inner.FindByVoiceLinkedIdAsync(tenantId, voiceLinkedId, ct);
        public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct) => inner.FindByVoiceLinkedIdAcrossTenantsAsync(voiceLinkedId, ct);
        public Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct) => inner.FindByIdAcrossTenantsAsync(conversationId, ct);
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
        public Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) => inner.TryClaimForInboundAsync(tenantId, conversationId, now, ct);
        public Task<bool> TryClaimForSendAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct) => inner.TryClaimForSendAsync(tenantId, conversationId, now, advanceClock, ct);
    }
}
