using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// whatsapp-works-for-real block B (tasks 3.1-3.4; message-delivery-correlation): on a real Postgres built
/// from the real migrations — one stored row per provider message id even under concurrent identical
/// deliveries, migration 020 over a database that already holds duplicates, the provider id and <c>Sent</c>
/// stamped in one write, monotonic delivery status, and the newest inbound message for the 24-hour window.
/// </summary>
public sealed class PostgresMessageCorrelationTests : IClassFixture<MessageCorrelationFixture>
{
    private static readonly TenantId Tenant = new("t-corr");

    private readonly MessageCorrelationFixture _fixture;

    public PostgresMessageCorrelationTests(MessageCorrelationFixture fixture) => _fixture = fixture;

    private static Message Inbound(EntityId conversationId, string? externalId, DateTimeOffset createdAt) => new()
    {
        MessageId = EntityId.New(),
        ConversationId = conversationId,
        TenantId = Tenant,
        Direction = MessageDirection.Inbound,
        Channel = ChannelType.WhatsApp,
        SenderId = "+573001112233",
        Content = new MessageEnvelope([new TextBlock("hola")]),
        DeliveryStatus = MessageDeliveryStatus.Delivered,
        ExternalMessageId = externalId,
        CreatedAt = createdAt,
        DeliveredAt = createdAt,
    };

    private static Message Outbound(EntityId conversationId, DateTimeOffset createdAt) => new()
    {
        MessageId = EntityId.New(),
        ConversationId = conversationId,
        TenantId = Tenant,
        Direction = MessageDirection.Outbound,
        Channel = ChannelType.WhatsApp,
        SenderId = "agent-1",
        Content = new MessageEnvelope([new TextBlock("reply")]),
        DeliveryStatus = MessageDeliveryStatus.Pending,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task FindByExternalIdAsync_ShouldReturnOne_WhenTwoConcurrentSavesRace()
    {
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var conversationId = EntityId.New();
        var now = DateTimeOffset.UtcNow;

        // Eight concurrent identical deliveries of one provider message, each building its own row.
        var stored = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => store.InsertInboundIfAbsentAsync(Inbound(conversationId, "wamid.race", now), CancellationToken.None))));

        var found = await store.FindByExternalIdAsync(Tenant, "wamid.race", CancellationToken.None);
        found.Should().NotBeNull();
        stored.Select(m => m.MessageId).Distinct().Should().ContainSingle()
            .Which.Should().Be(found!.MessageId, "every racer sees the one stored message");
        (await store.GetConversationMessagesAsync(Tenant, conversationId, 50, 0, CancellationToken.None))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task InsertInboundIfAbsentAsync_ShouldInsertEach_WhenMessagesHaveNoProviderId()
    {
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var conversationId = EntityId.New();
        var now = DateTimeOffset.UtcNow;

        await store.InsertInboundIfAbsentAsync(Inbound(conversationId, null, now), CancellationToken.None);
        await store.InsertInboundIfAbsentAsync(Inbound(conversationId, null, now.AddSeconds(1)), CancellationToken.None);

        (await store.GetConversationMessagesAsync(Tenant, conversationId, 50, 0, CancellationToken.None))
            .Should().HaveCount(2, "the unique index is partial: rows without a provider id never conflict");
    }

    [Fact]
    public async Task Migration020_ShouldSucceed_WhenDuplicatesExist()
    {
        var ds = await _fixture.CreateDatabaseAsync(before020: true);
        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-late", "wamid.dup", t0.AddSeconds(2));
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-early", "wamid.dup", t0);
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-later", "wamid.dup", t0.AddSeconds(5));
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-b", "m-other-tenant", "wamid.dup", t0.AddSeconds(9));
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-null-1", null, t0);
        await MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-null-2", null, t0);

        await MessageCorrelationFixture.ApplyMigrationAsync(ds, MessageCorrelationFixture.Migration020);

        (await RemainingIdsAsync(ds)).Should().Equal(
            ["m-early", "m-null-1", "m-null-2", "m-other-tenant"],
            "the earliest row per (tenant, provider id) is kept; another tenant's row and rows without a provider id are untouched");
        (await MessageCorrelationFixture.IndexExistsAsync(ds, "ux_messages_tenant_external")).Should().BeTrue();
        (await MessageCorrelationFixture.IndexExistsAsync(ds, "idx_messages_external")).Should().BeFalse();
        var duplicate = () => MessageCorrelationFixture.InsertRawAsync(ds, "tenant-a", "m-new", "wamid.dup", t0.AddMinutes(1));
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ux_messages_tenant_external");
    }

    [Fact]
    public async Task MarkSentAsync_ShouldPersistIdAndSent_WhenMessageExists()
    {
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var message = Outbound(EntityId.New(), DateTimeOffset.UtcNow);
        await store.SaveAsync(message, CancellationToken.None);

        await store.MarkSentAsync(Tenant, message.MessageId, "wamid.out-1", CancellationToken.None);

        var found = await store.FindByExternalIdAsync(Tenant, "wamid.out-1", CancellationToken.None);
        found.Should().NotBeNull("a status webhook right after the send must correlate");
        found!.MessageId.Should().Be(message.MessageId);
        found.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
    }

    [Fact]
    public async Task MarkSentAsync_ShouldKeepLaterStatus_WhenMessageAlreadyFailed()
    {
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var message = Outbound(EntityId.New(), DateTimeOffset.UtcNow);
        await store.SaveAsync(message, CancellationToken.None);
        await store.UpdateDeliveryStatusAsync(Tenant, message.MessageId, MessageDeliveryStatus.Failed, null, CancellationToken.None);

        await store.MarkSentAsync(Tenant, message.MessageId, "wamid.out-2", CancellationToken.None);

        var read = await store.GetByIdAsync(Tenant, message.MessageId, CancellationToken.None);
        read!.DeliveryStatus.Should().Be(MessageDeliveryStatus.Failed);
        read.ExternalMessageId.Should().Be("wamid.out-2");
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
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var message = Outbound(EntityId.New(), DateTimeOffset.UtcNow);
        message.DeliveryStatus = current;
        await store.SaveAsync(message, CancellationToken.None);
        var at = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

        await store.UpdateDeliveryStatusAsync(Tenant, message.MessageId, incoming, at, CancellationToken.None);

        var read = await store.GetByIdAsync(Tenant, message.MessageId, CancellationToken.None);
        read!.DeliveryStatus.Should().Be(expected);
        if (expected != current && incoming == MessageDeliveryStatus.Read)
            read.ReadAt.Should().Be(at);
        if (expected == current)
            (read.DeliveredAt, read.ReadAt).Should().Be((null, null), "an ignored update writes no timestamp either");
    }

    [Fact]
    public async Task FindLastInboundAsync_ShouldReturnNewestInbound_WhenOutboundIsNewer()
    {
        var store = new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
        var conversationId = EntityId.New();
        var t0 = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Inbound(conversationId, "wamid.in-1", t0), CancellationToken.None);
        var newest = Inbound(conversationId, "wamid.in-2", t0.AddMinutes(10));
        await store.SaveAsync(newest, CancellationToken.None);
        await store.SaveAsync(Outbound(conversationId, t0.AddMinutes(30)), CancellationToken.None);
        await store.SaveAsync(Inbound(EntityId.New(), "wamid.other-conv", t0.AddHours(1)), CancellationToken.None);

        var last = await store.FindLastInboundAsync(Tenant, conversationId, CancellationToken.None);

        last.Should().NotBeNull();
        last!.MessageId.Should().Be(newest.MessageId);
        last.CreatedAt.Should().Be(newest.CreatedAt);
        (await store.FindLastInboundAsync(Tenant, EntityId.New(), CancellationToken.None)).Should().BeNull();
    }

    private static async Task<List<string>> RemainingIdsAsync(NpgsqlDataSource ds)
    {
        var ids = new List<string>();
        await using var cmd = ds.CreateCommand("SELECT message_id FROM messages ORDER BY message_id");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }
}
