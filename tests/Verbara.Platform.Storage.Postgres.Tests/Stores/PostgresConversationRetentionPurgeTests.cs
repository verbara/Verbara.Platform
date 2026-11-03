using FluentAssertions;
using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// retention-purges-closed-conversations-only — the retention purge's two store calls, in the order
/// <c>RetentionPurgeService</c> makes them, on a real Postgres built from the real migrations: the conversation
/// delete keeps every open conversation and counts the window from <c>closed_at</c>, and the orphan-message
/// cleanup that follows removes only the messages of the conversations it deleted. Live-DB (Testcontainers):
/// runs in CI's "Live-DB Tests (Postgres)" job, not in the unit-test job.
/// </summary>
public sealed class PostgresConversationRetentionPurgeTests : IClassFixture<MessageCorrelationFixture>
{
    private static readonly TenantId Tenant = new("t-retention");
    private static readonly TenantId OtherTenant = new("t-retention-other");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddDays(-90);

    private readonly MessageCorrelationFixture _fixture;

    public PostgresConversationRetentionPurgeTests(MessageCorrelationFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldKeepOpenConversationAndItsMessages_WhenCreatedBeforeCutoff()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var open = await SaveAsync(conversations, ConversationState.Active, Now.AddDays(-200), closedAt: null);
        await AddMessageAsync(messages, open);

        var deleted = await conversations.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);
        var orphans = await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        deleted.Should().Be(0, "an open conversation is never purged, however old");
        orphans.Should().Be(0);
        (await conversations.GetByIdAsync(Tenant, open, CancellationToken.None)).Should().NotBeNull();
        (await CountMessagesAsync(ds, open)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldKeepConversation_WhenCreatedBeforeCutoffButClosedAfter()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var closedRecently = await SaveAsync(conversations, ConversationState.Closed, Now.AddDays(-200), Now.AddDays(-10));

        var deleted = await conversations.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);

        deleted.Should().Be(0, "the retention window counts from the close, not the creation");
        (await conversations.GetByIdAsync(Tenant, closedRecently, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldDeleteEveryTerminalStateAndItsMessages_WhenClosedBeforeCutoff()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var terminal = new List<EntityId>();
        foreach (var state in new[] { ConversationState.Closed, ConversationState.Abandoned, ConversationState.Merged, ConversationState.Spam })
        {
            var id = await SaveAsync(conversations, state, Now.AddDays(-200), Now.AddDays(-120));
            await AddMessageAsync(messages, id);
            terminal.Add(id);
        }

        var open = await SaveAsync(conversations, ConversationState.WaitingForCustomer, Now.AddDays(-200), closedAt: null);
        await AddMessageAsync(messages, open);
        var otherTenant = await SaveAsync(conversations, ConversationState.Closed, Now.AddDays(-200), Now.AddDays(-120), OtherTenant);

        var deleted = await conversations.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);
        var orphans = await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        deleted.Should().Be(4, "Closed, Abandoned, Merged and Spam are all terminal");
        orphans.Should().Be(4, "each purged conversation's message goes with it");
        foreach (var id in terminal)
            (await conversations.GetByIdAsync(Tenant, id, CancellationToken.None)).Should().BeNull();
        (await conversations.GetByIdAsync(Tenant, open, CancellationToken.None)).Should().NotBeNull();
        (await CountMessagesAsync(ds, open)).Should().Be(1, "the kept conversation keeps its messages");
        (await conversations.GetByIdAsync(OtherTenant, otherTenant, CancellationToken.None))
            .Should().NotBeNull("the purge is tenant-scoped");
    }

    // Review round 2: Resolved never becomes Closed on its own, so it is purged on last activity (updated_at,
    // falling back to created_at when NULL); every live state stays however idle.
    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldPurgeIdleResolvedAndKeepRecentResolvedAndLive_WhenPastCutoff()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var idle = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            updatedAt: Now.AddDays(-120));
        await AddMessageAsync(messages, idle);
        var legacy = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            updatedAt: null, stampUpdatedAt: false);
        var recent = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            updatedAt: Now.AddDays(-10));
        await AddMessageAsync(messages, recent);
        var live = new List<EntityId>();
        foreach (var state in new[]
                 {
                     ConversationState.Queued, ConversationState.Active, ConversationState.WrapUp,
                     ConversationState.WaitingForCustomer, ConversationState.Snoozed, ConversationState.Escalated,
                 })
        {
            live.Add(await SaveAsync(conversations, state, Now.AddDays(-200), closedAt: null, updatedAt: Now.AddDays(-120)));
        }

        var otherTenant = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            tenant: OtherTenant, updatedAt: Now.AddDays(-120));

        var deleted = await conversations.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);
        var orphans = await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        deleted.Should().Be(2, "the idle Resolved row and the one with no updated_at (created long ago) are purged");
        orphans.Should().Be(1, "the idle Resolved conversation's message goes with it");
        (await conversations.GetByIdAsync(Tenant, idle, CancellationToken.None)).Should().BeNull();
        (await conversations.GetByIdAsync(Tenant, legacy, CancellationToken.None)).Should().BeNull();
        (await conversations.GetByIdAsync(Tenant, recent, CancellationToken.None)).Should().NotBeNull();
        (await CountMessagesAsync(ds, recent)).Should().Be(1);
        foreach (var id in live)
            (await conversations.GetByIdAsync(Tenant, id, CancellationToken.None))
                .Should().NotBeNull("a live conversation is never purged, however idle");
        (await conversations.GetByIdAsync(OtherTenant, otherTenant, CancellationToken.None))
            .Should().NotBeNull("the purge is tenant-scoped");
    }

    // Review round 3: a customer reply to a Resolved conversation is appended as a message without touching the
    // conversation row, so updated_at stays old; a message of that tenant at or after the cutoff keeps it.
    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldKeepResolvedAndItsMessages_WhenIdleRowButMessagedAfterCutoff()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        var conversations = new PostgresConversationStore(ds);
        var messages = new PostgresMessageStore(ds);
        var replied = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            updatedAt: Now.AddDays(-120));
        await AddMessageAsync(messages, replied, Now.AddDays(-150));
        await AddMessageAsync(messages, replied, Now.AddDays(-5));
        var idle = await SaveAsync(conversations, ConversationState.Resolved, Now.AddDays(-200), closedAt: null,
            updatedAt: Now.AddDays(-120));
        await AddMessageAsync(messages, idle, Now.AddDays(-150));
        // Same conversation id, other tenant, recent: must not shield this tenant's idle row.
        await AddMessageAsync(messages, idle, Now.AddDays(-5), OtherTenant);

        var deleted = await conversations.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);
        var orphans = await messages.DeleteOrphanedAsync(Tenant, CancellationToken.None);

        deleted.Should().Be(1, "only the Resolved conversation with no message inside the window is purged");
        orphans.Should().Be(1, "the purged conversation's old message goes with it");
        (await conversations.GetByIdAsync(Tenant, replied, CancellationToken.None))
            .Should().NotBeNull("a message inside the window is recent activity, though the row itself is old");
        (await CountMessagesAsync(ds, replied)).Should().Be(2);
        (await conversations.GetByIdAsync(Tenant, idle, CancellationToken.None)).Should().BeNull();
    }

    private static async Task<EntityId> SaveAsync(
        PostgresConversationStore store, ConversationState state, DateTimeOffset createdAt, DateTimeOffset? closedAt,
        TenantId? tenant = null, DateTimeOffset? updatedAt = null, bool stampUpdatedAt = true)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = tenant ?? Tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WhatsApp,
            State = state,
            CreatedAt = createdAt,
            ClosedAt = closedAt,
            UpdatedAt = stampUpdatedAt ? updatedAt ?? closedAt ?? createdAt : null,
        };
        await store.SaveAsync(conversation, CancellationToken.None);
        return conversation.ConversationId;
    }

    private static Task AddMessageAsync(
        PostgresMessageStore store, EntityId conversationId, DateTimeOffset? createdAt = null, TenantId? tenant = null) =>
        store.SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = tenant ?? Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = createdAt ?? Now.AddDays(-200),
        }, CancellationToken.None);

    private static async Task<long> CountMessagesAsync(NpgsqlDataSource ds, EntityId conversationId)
    {
        await using var cmd = ds.CreateCommand("SELECT COUNT(*) FROM messages WHERE conversation_id = @Id");
        cmd.Parameters.Add(new NpgsqlParameter("Id", conversationId.Value));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
