using System.Text.Json;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class GdprStoreExtensionTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private static readonly EntityId Contact1 = EntityId.New();
    private static readonly EntityId Contact2 = EntityId.New();

    // ─── ConversationStore ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListByContactAsync_ShouldReturnConversationsForContact()
    {
        var store = new InMemoryConversationStore();
        var c1 = MakeConversation(Contact1);
        var c2 = MakeConversation(Contact1);
        var c3 = MakeConversation(Contact2);
        await store.SaveAsync(c1, CancellationToken.None);
        await store.SaveAsync(c2, CancellationToken.None);
        await store.SaveAsync(c3, CancellationToken.None);

        var result = await store.ListByContactAsync(Tenant, Contact1, CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteByContactAsync_ShouldDeleteAndReturnCount()
    {
        var store = new InMemoryConversationStore();
        await store.SaveAsync(MakeConversation(Contact1), CancellationToken.None);
        await store.SaveAsync(MakeConversation(Contact1), CancellationToken.None);
        await store.SaveAsync(MakeConversation(Contact2), CancellationToken.None);

        var deleted = await store.DeleteByContactAsync(Tenant, Contact1, CancellationToken.None);

        deleted.Should().Be(2);
        var remaining = await store.ListByContactAsync(Tenant, Contact1, CancellationToken.None);
        remaining.Should().BeEmpty();
    }

    // retention-purges-closed-conversations-only: the retention clock is the close, and only a
    // terminal conversation (Closed, Abandoned, Merged, Spam) is ever purged.

    private static readonly DateTimeOffset PurgeNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = PurgeNow.AddDays(-90);

    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldKeepConversation_WhenOpenAndCreatedBeforeCutoff()
    {
        var store = new InMemoryConversationStore();
        var open = MakeConversation(Contact1, createdAt: PurgeNow.AddDays(-200));
        await store.SaveAsync(open, CancellationToken.None);

        var deleted = await store.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);

        deleted.Should().Be(0, "an open conversation is never purged, however old");
        (await store.GetByIdAsync(Tenant, open.ConversationId, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteClosedBeforeAsync_ShouldKeepConversation_WhenCreatedBeforeCutoffButClosedAfter()
    {
        var store = new InMemoryConversationStore();
        var closedRecently = MakeClosed(ConversationState.Closed, createdAt: PurgeNow.AddDays(-200), closedAt: PurgeNow.AddDays(-10));
        await store.SaveAsync(closedRecently, CancellationToken.None);

        var deleted = await store.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);

        deleted.Should().Be(0, "the retention window counts from the close, not the creation");
        (await store.GetByIdAsync(Tenant, closedRecently.ConversationId, CancellationToken.None)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(ConversationState.Closed)]
    [InlineData(ConversationState.Abandoned)]
    [InlineData(ConversationState.Merged)]
    [InlineData(ConversationState.Spam)]
    public async Task DeleteClosedBeforeAsync_ShouldDeleteConversation_WhenTerminalAndClosedBeforeCutoff(ConversationState state)
    {
        var store = new InMemoryConversationStore();
        var old = MakeClosed(state, createdAt: PurgeNow.AddDays(-200), closedAt: PurgeNow.AddDays(-120));
        var otherTenant = MakeClosed(state, createdAt: PurgeNow.AddDays(-200), closedAt: PurgeNow.AddDays(-120), tenant: new TenantId("tenant-2"));
        await store.SaveAsync(old, CancellationToken.None);
        await store.SaveAsync(otherTenant, CancellationToken.None);

        var deleted = await store.DeleteClosedBeforeAsync(Tenant, Cutoff, CancellationToken.None);

        deleted.Should().Be(1);
        (await store.GetByIdAsync(Tenant, old.ConversationId, CancellationToken.None)).Should().BeNull();
        (await store.GetByIdAsync(otherTenant.TenantId, otherTenant.ConversationId, CancellationToken.None))
            .Should().NotBeNull("the purge is tenant-scoped");
    }

    // ─── MessageStore ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByConversationIdsAsync_ShouldReturnMessagesAcrossConversations()
    {
        var store = new InMemoryMessageStore();
        var conv1 = EntityId.New();
        var conv2 = EntityId.New();
        var conv3 = EntityId.New();
        await store.SaveAsync(MakeMessage(conv1), CancellationToken.None);
        await store.SaveAsync(MakeMessage(conv2), CancellationToken.None);
        await store.SaveAsync(MakeMessage(conv3), CancellationToken.None);

        var result = await store.GetByConversationIdsAsync(Tenant, [conv1, conv2], CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteByConversationIdsAsync_ShouldDeleteAndReturnCount()
    {
        var store = new InMemoryMessageStore();
        var conv1 = EntityId.New();
        var conv2 = EntityId.New();
        await store.SaveAsync(MakeMessage(conv1), CancellationToken.None);
        await store.SaveAsync(MakeMessage(conv1), CancellationToken.None);
        await store.SaveAsync(MakeMessage(conv2), CancellationToken.None);

        var deleted = await store.DeleteByConversationIdsAsync(Tenant, [conv1], CancellationToken.None);

        deleted.Should().Be(2);
    }

    // ─── AuthEventStore ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAllByUserAsync_ShouldReturnAllEventsForUser()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeAuthEvent("user-1"), CancellationToken.None);
        await store.SaveAsync(MakeAuthEvent("user-1"), CancellationToken.None);
        await store.SaveAsync(MakeAuthEvent("user-2"), CancellationToken.None);

        var result = await store.ListAllByUserAsync("tenant-1", "user-1", CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteByUserAsync_ShouldDeleteAndReturnCount()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeAuthEvent("user-1"), CancellationToken.None);
        await store.SaveAsync(MakeAuthEvent("user-1"), CancellationToken.None);
        await store.SaveAsync(MakeAuthEvent("user-2"), CancellationToken.None);

        var deleted = await store.DeleteByUserAsync("tenant-1", "user-1", CancellationToken.None);

        deleted.Should().Be(2);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_ShouldDeleteOldAuthEvents()
    {
        var store = new InMemoryAuthEventStore();
        await store.SaveAsync(MakeAuthEvent("user-1", DateTimeOffset.UtcNow.AddDays(-90)), CancellationToken.None);
        await store.SaveAsync(MakeAuthEvent("user-1", DateTimeOffset.UtcNow), CancellationToken.None);

        var deleted = await store.DeleteOlderThanAsync("tenant-1", DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);

        deleted.Should().Be(1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static Conversation MakeConversation(EntityId contactId, DateTimeOffset? createdAt = null) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = Tenant,
        ContactId = contactId,
        Channel = ChannelType.WebChat,
        State = ConversationState.Active,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private static Conversation MakeClosed(
        ConversationState state, DateTimeOffset createdAt, DateTimeOffset closedAt, TenantId? tenant = null) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = tenant ?? Tenant,
        ContactId = Contact1,
        Channel = ChannelType.WebChat,
        State = state,
        CreatedAt = createdAt,
        ClosedAt = closedAt,
    };

    private static Message MakeMessage(EntityId conversationId) => new()
    {
        MessageId = EntityId.New(),
        ConversationId = conversationId,
        TenantId = Tenant,
        Direction = MessageDirection.Inbound,
        Channel = ChannelType.WebChat,
        Content = new MessageEnvelope([new TextBlock("test")]),
        DeliveryStatus = MessageDeliveryStatus.Sent,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AuthEvent MakeAuthEvent(string userId, DateTimeOffset? createdAt = null) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        TenantId = "tenant-1",
        UserId = userId,
        EventType = "login_success",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };
}
