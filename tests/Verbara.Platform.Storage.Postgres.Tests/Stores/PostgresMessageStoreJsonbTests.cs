using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Reads message history back from a real Postgres <c>messages.content</c> JSONB column.
/// JSONB does not keep object key order, so the <see cref="MessageBlock"/> type
/// discriminator <c>$type</c> is no longer the first property of each block when it is read
/// back. Before 2.25.1 every read below threw <see cref="NotSupportedException"/>
/// ("must specify a type discriminator") from the source-generated deserializer.
/// </summary>
public class PostgresMessageStoreJsonbTests : IClassFixture<MessageStoreFixture>, IAsyncLifetime
{
    private readonly MessageStoreFixture _fixture;
    private readonly PostgresMessageStore _sut;
    private readonly TenantId _tenant;

    public PostgresMessageStoreJsonbTests(MessageStoreFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresMessageStore(_fixture.DataSource);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Message MakeMessage(
        EntityId conversationId, string? externalId = null, DateTimeOffset? createdAt = null, MessageEnvelope? content = null) =>
        new()
        {
            MessageId = EntityId.New(),
            ConversationId = conversationId,
            TenantId = _tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            SenderId = "contact-1",
            Content = content ?? new MessageEnvelope(
            [
                new TextBlock("hello"),
                new ImageBlock("https://cdn.example.com/a.jpg", "a photo", "image/jpeg"),
            ]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = externalId,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    private static void AssertTextAndImage(Message message)
    {
        message.Content.Blocks.Should().HaveCount(2);
        message.Content.Blocks[0].Should().BeOfType<TextBlock>().Which.Text.Should().Be("hello");
        var image = message.Content.Blocks[1].Should().BeOfType<ImageBlock>().Subject;
        image.Url.Should().Be("https://cdn.example.com/a.jpg");
        image.Caption.Should().Be("a photo");
        image.MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreDiscriminatorOutOfFirstPosition_WhenContentIsJsonb()
    {
        // Pins the precondition the read tests depend on: if Postgres ever kept key order,
        // the tests below would pass without exercising the bug.
        var message = MakeMessage(EntityId.New());
        await _sut.SaveAsync(message, CancellationToken.None);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (content->'blocks'->0)::text FROM messages WHERE tenant_id = @t AND message_id = @m";
        cmd.Parameters.Add(new NpgsqlParameter("t", _tenant.Value));
        cmd.Parameters.Add(new NpgsqlParameter("m", message.MessageId.Value));

        var storedBlock = (string?)await cmd.ExecuteScalarAsync();

        storedBlock.Should().Contain("\"$type\": \"text\"");
        storedBlock.Should().NotStartWith("{\"$type\"");
    }

    [Fact]
    public async Task GetConversationMessagesAsync_ShouldReturnTextAndImageBlocks_WhenContentIsJsonb()
    {
        var conversationId = EntityId.New();
        await _sut.SaveAsync(MakeMessage(conversationId), CancellationToken.None);

        var messages = await _sut.GetConversationMessagesAsync(_tenant, conversationId, 50, 0, CancellationToken.None);

        AssertTextAndImage(messages.Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnTextAndImageBlocks_WhenContentIsJsonb()
    {
        var message = MakeMessage(EntityId.New());
        await _sut.SaveAsync(message, CancellationToken.None);

        var read = await _sut.GetByIdAsync(_tenant, message.MessageId, CancellationToken.None);

        read.Should().NotBeNull();
        AssertTextAndImage(read!);
    }

    [Fact]
    public async Task FindByExternalIdAsync_ShouldReturnTextAndImageBlocks_WhenContentIsJsonb()
    {
        var message = MakeMessage(EntityId.New(), externalId: "wamid.HBgM-roundtrip");
        await _sut.SaveAsync(message, CancellationToken.None);

        var read = await _sut.FindByExternalIdAsync(_tenant, "wamid.HBgM-roundtrip", CancellationToken.None);

        read.Should().NotBeNull();
        read!.MessageId.Should().Be(message.MessageId);
        AssertTextAndImage(read);
    }

    [Fact]
    public async Task GetByConversationIdsAsync_ShouldReturnTextAndImageBlocks_WhenContentIsJsonb()
    {
        var first = EntityId.New();
        var second = EntityId.New();
        var now = DateTimeOffset.UtcNow;
        await _sut.SaveAsync(MakeMessage(first, createdAt: now), CancellationToken.None);
        await _sut.SaveAsync(MakeMessage(second, createdAt: now.AddSeconds(1)), CancellationToken.None);

        var messages = await _sut.GetByConversationIdsAsync(_tenant, [first, second], CancellationToken.None);

        messages.Should().HaveCount(2);
        messages.Select(m => m.ConversationId).Should().Equal(first, second);
        messages.Should().AllSatisfy(AssertTextAndImage);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnEveryBlockType_WhenContentIsJsonb()
    {
        var message = MakeMessage(EntityId.New(), content: new MessageEnvelope(
            [
                new TextBlock("t"),
                new ImageBlock("https://x/i.png", null, "image/png"),
                new AudioBlock("https://x/a.ogg", TimeSpan.FromSeconds(7), "audio/ogg"),
                new VideoBlock("https://x/v.mp4", "clip", null),
                new FileBlock("https://x/f.pdf", "f.pdf", "application/pdf", 1024L),
                new LocationBlock(4.65, -74.05, "Bogota"),
                new InteractiveBlock("pick one", [new QuickReply("y", "Yes"), new QuickReply("n", "No")]),
            ]));
        await _sut.SaveAsync(message, CancellationToken.None);

        var read = await _sut.GetByIdAsync(_tenant, message.MessageId, CancellationToken.None);

        read.Should().NotBeNull();
        read!.Content.Blocks.Select(b => b.GetType()).Should().Equal(
            typeof(TextBlock), typeof(ImageBlock), typeof(AudioBlock), typeof(VideoBlock),
            typeof(FileBlock), typeof(LocationBlock), typeof(InteractiveBlock));
        read.Content.Blocks[2].Should().BeOfType<AudioBlock>().Which.Duration.Should().Be(TimeSpan.FromSeconds(7));
        read.Content.Blocks[5].Should().BeOfType<LocationBlock>().Which.Name.Should().Be("Bogota");
        read.Content.Blocks[6].Should().BeOfType<InteractiveBlock>().Which.Replies.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReadRow_WhenWrittenByEarlierRelease()
    {
        // Rows persisted by <= 2.25.0 are stored exactly like this (JSONB key order); no
        // migration is needed — the fix is on the read side only.
        var messageId = EntityId.New();
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO messages (message_id, conversation_id, tenant_id, direction, channel, " +
                              "content, delivery_status, created_at) VALUES (@m, @c, @t, 0, 1, " +
                              "'{\"blocks\": [{\"text\": \"legacy\", \"type\": 0, \"$type\": \"text\"}]}'::jsonb, 0, now())";
            cmd.Parameters.Add(new NpgsqlParameter("m", messageId.Value));
            cmd.Parameters.Add(new NpgsqlParameter("c", EntityId.New().Value));
            cmd.Parameters.Add(new NpgsqlParameter("t", _tenant.Value));
            await cmd.ExecuteNonQueryAsync();
        }

        var read = await _sut.GetByIdAsync(_tenant, messageId, CancellationToken.None);

        read.Should().NotBeNull();
        read!.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>()
            .Which.Text.Should().Be("legacy");
    }
}
