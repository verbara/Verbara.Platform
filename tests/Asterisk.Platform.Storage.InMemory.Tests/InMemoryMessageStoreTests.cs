using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryMessageStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static Message MakeMessage(TenantId tenantId, EntityId conversationId, string? externalId = null)
    {
        return new Message
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = tenantId,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WebChat,
            Content = new MessageEnvelope([new TextBlock("hello")]),
            DeliveryStatus = MessageDeliveryStatus.Sent,
            ExternalMessageId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task FindByExternalIdAsync_ShouldReturnMessage_WhenExternalIdMatches()
    {
        var store = new InMemoryMessageStore();
        var convId = EntityId.New();
        var msg = MakeMessage(Tenant, convId, "ext-123");
        await store.SaveAsync(msg, CancellationToken.None);

        var result = await store.FindByExternalIdAsync(Tenant, "ext-123", CancellationToken.None);

        result.Should().BeSameAs(msg);
    }

    [Fact]
    public async Task FindByExternalIdAsync_ShouldReturnNull_WhenNotFound()
    {
        var store = new InMemoryMessageStore();

        var result = await store.FindByExternalIdAsync(Tenant, "missing", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetConversationMessagesAsync_ShouldReturnPaginatedMessages()
    {
        var store = new InMemoryMessageStore();
        var convId = EntityId.New();

        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakeMessage(Tenant, convId), CancellationToken.None);

        // different conversation — should not appear
        await store.SaveAsync(MakeMessage(Tenant, EntityId.New()), CancellationToken.None);

        var page1 = await store.GetConversationMessagesAsync(Tenant, convId, limit: 2, offset: 0, CancellationToken.None);
        var page2 = await store.GetConversationMessagesAsync(Tenant, convId, limit: 2, offset: 2, CancellationToken.None);
        var page3 = await store.GetConversationMessagesAsync(Tenant, convId, limit: 2, offset: 4, CancellationToken.None);

        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);
        page3.Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateDeliveryStatusAsync_ShouldChangeStatus()
    {
        var store = new InMemoryMessageStore();
        var convId = EntityId.New();
        var msg = MakeMessage(Tenant, convId);
        await store.SaveAsync(msg, CancellationToken.None);

        var ts = DateTimeOffset.UtcNow;
        await store.UpdateDeliveryStatusAsync(Tenant, msg.MessageId, MessageDeliveryStatus.Delivered, ts, CancellationToken.None);

        var retrieved = await store.GetByIdAsync(Tenant, msg.MessageId, CancellationToken.None);
        retrieved!.DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
        retrieved.DeliveredAt.Should().Be(ts);
    }
}
