using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryConversationStoreTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");

    private static Conversation MakeConversation(TenantId tenantId, ConversationState state = ConversationState.Active)
    {
        var contactId = EntityId.New();
        return new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = tenantId,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistConversation()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Tenant1);

        await store.SaveAsync(conv, CancellationToken.None);

        var retrieved = await store.GetByIdAsync(Tenant1, conv.ConversationId, CancellationToken.None);
        retrieved.Should().BeSameAs(conv);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = new InMemoryConversationStore();

        var result = await store.GetByIdAsync(Tenant1, EntityId.New(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnAllForTenant_WhenNoFilters()
    {
        var store = new InMemoryConversationStore();
        var conv1 = MakeConversation(Tenant1);
        var conv2 = MakeConversation(Tenant1);
        var conv3 = MakeConversation(Tenant2);

        await store.SaveAsync(conv1, CancellationToken.None);
        await store.SaveAsync(conv2, CancellationToken.None);
        await store.SaveAsync(conv3, CancellationToken.None);

        var result = await store.ListAsync(Tenant1, new ConversationQuery { Page = 1, PageSize = 25 }, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task ListAsync_ShouldFilterByState()
    {
        var store = new InMemoryConversationStore();
        var active = MakeConversation(Tenant1, ConversationState.Active);
        var closed = MakeConversation(Tenant1, ConversationState.Closed);

        await store.SaveAsync(active, CancellationToken.None);
        await store.SaveAsync(closed, CancellationToken.None);

        var result = await store.ListAsync(Tenant1, new ConversationQuery { State = ConversationState.Active }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].ConversationId.Should().Be(active.ConversationId);
    }

    [Fact]
    public async Task ListAsync_ShouldSupportPaging()
    {
        var store = new InMemoryConversationStore();
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakeConversation(Tenant1), CancellationToken.None);

        var page1 = await store.ListAsync(Tenant1, new ConversationQuery { Page = 1, PageSize = 2 }, CancellationToken.None);
        var page2 = await store.ListAsync(Tenant1, new ConversationQuery { Page = 2, PageSize = 2 }, CancellationToken.None);
        var page3 = await store.ListAsync(Tenant1, new ConversationQuery { Page = 3, PageSize = 2 }, CancellationToken.None);

        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page3.Items.Should().HaveCount(1);
        page1.TotalCount.Should().Be(5);
        page1.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task FindActiveByContactAsync_ShouldReturnNonTerminalConversation()
    {
        var store = new InMemoryConversationStore();
        var contactId = EntityId.New();
        var active = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant1,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(active, CancellationToken.None);

        var result = await store.FindActiveByContactAsync(Tenant1, contactId, ChannelType.WebChat, CancellationToken.None);

        result.Should().BeSameAs(active);
    }

    [Fact]
    public async Task FindActiveByContactAsync_ShouldReturnNull_WhenOnlyTerminalExists()
    {
        var store = new InMemoryConversationStore();
        var contactId = EntityId.New();
        var closed = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = Tenant1,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(closed, CancellationToken.None);

        var result = await store.FindActiveByContactAsync(Tenant1, contactId, ChannelType.WebChat, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Tenant1);
        await store.SaveAsync(conv, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant2, conv.ConversationId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldOverwriteExistingConversation()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeConversation(Tenant1, ConversationState.Queued);
        await store.SaveAsync(conv, CancellationToken.None);

        conv.State = ConversationState.Active;
        await store.SaveAsync(conv, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, conv.ConversationId, CancellationToken.None);
        result!.State.Should().Be(ConversationState.Active);
    }
}
