using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

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

    private static Conversation MakeVoiceConversation(TenantId tenantId, string linkedId, ConversationState state = ConversationState.Queued) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = tenantId,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = linkedId,
        };

    [Fact]
    public async Task FindByVoiceLinkedIdAsync_ShouldReturnConversation_WhenLinkedIdMatches()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeVoiceConversation(Tenant1, "linked-abc");
        await store.SaveAsync(conv, CancellationToken.None);

        var result = await store.FindByVoiceLinkedIdAsync(Tenant1, "linked-abc", CancellationToken.None);

        result.Should().BeSameAs(conv);
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAsync_ShouldReturnNull_WhenNoMatch()
    {
        var store = new InMemoryConversationStore();
        await store.SaveAsync(MakeVoiceConversation(Tenant1, "linked-abc"), CancellationToken.None);

        var result = await store.FindByVoiceLinkedIdAsync(Tenant1, "linked-xyz", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAsync_ShouldIsolateTenants()
    {
        var store = new InMemoryConversationStore();
        await store.SaveAsync(MakeVoiceConversation(Tenant1, "linked-abc"), CancellationToken.None);

        var result = await store.FindByVoiceLinkedIdAsync(Tenant2, "linked-abc", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldNoOp_WhenDifferentConversationHasSameVoiceLinkedId()
    {
        // Mirrors the Postgres partial-unique index: one physical call ⇒ one Conversation.
        var store = new InMemoryConversationStore();
        var first = MakeVoiceConversation(Tenant1, "linked-dup");
        var duplicate = MakeVoiceConversation(Tenant1, "linked-dup");

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(duplicate, CancellationToken.None);

        var byFirstId = await store.GetByIdAsync(Tenant1, first.ConversationId, CancellationToken.None);
        var byDuplicateId = await store.GetByIdAsync(Tenant1, duplicate.ConversationId, CancellationToken.None);
        var byLinkedId = await store.FindByVoiceLinkedIdAsync(Tenant1, "linked-dup", CancellationToken.None);

        byFirstId.Should().BeSameAs(first);
        byDuplicateId.Should().BeNull("the second conversation with the same voice LinkedId is a no-op");
        byLinkedId.Should().BeSameAs(first);
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenSameVoiceConversationResaved()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeVoiceConversation(Tenant1, "linked-same", ConversationState.Queued);
        await store.SaveAsync(conv, CancellationToken.None);

        conv.State = ConversationState.Offered;
        await store.SaveAsync(conv, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant1, conv.ConversationId, CancellationToken.None);
        result!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAcrossTenantsAsync_ShouldFindRegardlessOfTenant()
    {
        var store = new InMemoryConversationStore();
        var conv = MakeVoiceConversation(Tenant2, "linked-xt");
        await store.SaveAsync(conv, CancellationToken.None);

        var result = await store.FindByVoiceLinkedIdAcrossTenantsAsync("linked-xt", CancellationToken.None);

        result.Should().BeSameAs(conv);
    }

    [Fact]
    public async Task SaveAsync_ShouldNotConstrainNullVoiceLinkedId()
    {
        // Non-voice conversations all have a null VoiceLinkedId and must not collide.
        var store = new InMemoryConversationStore();
        var a = MakeConversation(Tenant1);
        var b = MakeConversation(Tenant1);

        await store.SaveAsync(a, CancellationToken.None);
        await store.SaveAsync(b, CancellationToken.None);

        (await store.GetByIdAsync(Tenant1, a.ConversationId, CancellationToken.None)).Should().NotBeNull();
        (await store.GetByIdAsync(Tenant1, b.ConversationId, CancellationToken.None)).Should().NotBeNull();
    }
}
