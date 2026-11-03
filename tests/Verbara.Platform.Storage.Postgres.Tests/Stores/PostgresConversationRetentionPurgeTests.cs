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

    private static async Task<EntityId> SaveAsync(
        PostgresConversationStore store, ConversationState state, DateTimeOffset createdAt, DateTimeOffset? closedAt,
        TenantId? tenant = null)
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
            UpdatedAt = closedAt ?? createdAt,
        };
        await store.SaveAsync(conversation, CancellationToken.None);
        return conversation.ConversationId;
    }

    private static Task AddMessageAsync(PostgresMessageStore store, EntityId conversationId) =>
        store.SaveAsync(new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("hola")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = Now.AddDays(-200),
        }, CancellationToken.None);

    private static async Task<long> CountMessagesAsync(NpgsqlDataSource ds, EntityId conversationId)
    {
        await using var cmd = ds.CreateCommand("SELECT COUNT(*) FROM messages WHERE conversation_id = @Id");
        cmd.Parameters.Add(new NpgsqlParameter("Id", conversationId.Value));
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
