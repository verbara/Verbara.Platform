using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

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

    // ─── whatsapp-works-for-real block B (message-delivery-correlation) ─────────

    private static Message Outbound(EntityId conversationId, MessageDeliveryStatus status, DateTimeOffset createdAt) => new()
    {
        MessageId = EntityId.New(),
        ConversationId = conversationId,
        TenantId = Tenant,
        Direction = MessageDirection.Outbound,
        Channel = ChannelType.WhatsApp,
        Content = new MessageEnvelope([new TextBlock("reply")]),
        DeliveryStatus = status,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task MarkSentAsync_ShouldPersistIdAndSent_WhenMessageExists()
    {
        var store = new InMemoryMessageStore();
        var message = Outbound(EntityId.New(), MessageDeliveryStatus.Pending, DateTimeOffset.UtcNow);
        await store.SaveAsync(message, CancellationToken.None);

        await store.MarkSentAsync(Tenant, message.MessageId, "wamid.out-1", CancellationToken.None);

        var found = await store.FindByExternalIdAsync(Tenant, "wamid.out-1", CancellationToken.None);
        found.Should().NotBeNull();
        found!.MessageId.Should().Be(message.MessageId);
        found.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
    }

    [Fact]
    public async Task FindByExternalIdAsync_ShouldReturnOne_WhenTwoConcurrentSavesRace()
    {
        var store = new InMemoryMessageStore();
        var conversationId = EntityId.New();

        var stored = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            store.InsertInboundIfAbsentAsync(MakeMessage(Tenant, conversationId, "wamid.race"), CancellationToken.None))));

        stored.Select(m => m.MessageId).Distinct().Should().ContainSingle();
        (await store.GetConversationMessagesAsync(Tenant, conversationId, 50, 0, CancellationToken.None)).Should().ContainSingle();
        (await store.FindByExternalIdAsync(Tenant, "wamid.race", CancellationToken.None))!.MessageId
            .Should().Be(stored[0].MessageId);
    }

    [Fact]
    public async Task InsertInboundIfAbsentAsync_ShouldInsert_WhenSameProviderIdBelongsToAnotherTenant()
    {
        var store = new InMemoryMessageStore();
        var other = new TenantId("tenant-2");
        await store.InsertInboundIfAbsentAsync(MakeMessage(Tenant, EntityId.New(), "wamid.shared"), CancellationToken.None);

        var second = MakeMessage(other, EntityId.New(), "wamid.shared");
        var stored = await store.InsertInboundIfAbsentAsync(second, CancellationToken.None);

        stored.MessageId.Should().Be(second.MessageId);
    }

    [Theory]
    [InlineData(MessageDeliveryStatus.Read, MessageDeliveryStatus.Sent, MessageDeliveryStatus.Read)]
    [InlineData(MessageDeliveryStatus.Read, MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Read)]
    [InlineData(MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Sent, MessageDeliveryStatus.Delivered)]
    [InlineData(MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Failed, MessageDeliveryStatus.Delivered)]
    [InlineData(MessageDeliveryStatus.Failed, MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Failed)]
    [InlineData(MessageDeliveryStatus.Failed, MessageDeliveryStatus.Read, MessageDeliveryStatus.Failed)]
    [InlineData(MessageDeliveryStatus.Sent, MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Delivered)]
    [InlineData(MessageDeliveryStatus.Delivered, MessageDeliveryStatus.Read, MessageDeliveryStatus.Read)]
    [InlineData(MessageDeliveryStatus.Pending, MessageDeliveryStatus.Read, MessageDeliveryStatus.Read)]
    [InlineData(MessageDeliveryStatus.Sent, MessageDeliveryStatus.Failed, MessageDeliveryStatus.Failed)]
    public async Task UpdateDeliveryStatusAsync_ShouldIgnore_WhenStatusWouldMoveBackwards(
        MessageDeliveryStatus current, MessageDeliveryStatus incoming, MessageDeliveryStatus expected)
    {
        var store = new InMemoryMessageStore();
        var message = Outbound(EntityId.New(), current, DateTimeOffset.UtcNow);
        await store.SaveAsync(message, CancellationToken.None);

        await store.UpdateDeliveryStatusAsync(Tenant, message.MessageId, incoming, DateTimeOffset.UtcNow, CancellationToken.None);

        (await store.GetByIdAsync(Tenant, message.MessageId, CancellationToken.None))!.DeliveryStatus.Should().Be(expected);
    }

    [Fact]
    public async Task FindLastInboundAsync_ShouldReturnNewestInbound_WhenOutboundIsNewer()
    {
        var store = new InMemoryMessageStore();
        var conversationId = EntityId.New();
        var older = MakeMessage(Tenant, conversationId, "wamid.in-1");
        await store.SaveAsync(older, CancellationToken.None);
        var newest = new Message
        {
            MessageId = EntityId.New(), ConversationId = conversationId, TenantId = Tenant,
            Direction = MessageDirection.Inbound, Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("later")]), DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = older.CreatedAt.AddMinutes(10),
        };
        await store.SaveAsync(newest, CancellationToken.None);
        await store.SaveAsync(Outbound(conversationId, MessageDeliveryStatus.Sent, older.CreatedAt.AddMinutes(30)), CancellationToken.None);

        var last = await store.FindLastInboundAsync(Tenant, conversationId, CancellationToken.None);

        last!.MessageId.Should().Be(newest.MessageId);
        (await store.FindLastInboundAsync(Tenant, EntityId.New(), CancellationToken.None)).Should().BeNull();
    }
}
