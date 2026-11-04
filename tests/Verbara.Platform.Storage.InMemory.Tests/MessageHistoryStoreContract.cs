using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.TestSupport;

/// <summary>
/// contact-history-opens-by-assignment task 2.4 (design D10): <see cref="IMessageStore.GetConversationHistoryAsync"/> on
/// every store. Newest first by <c>created_at</c> then message id descending (ordinal), at most the limit with
/// <see cref="MessageHistorySlice.HasMore"/> decided by one extra row, only strictly older messages after a cursor,
/// nothing repeated or skipped when a message lands between two pages, and the tenant and conversation always pinned:
/// a cursor of another conversation only bounds the keyset.
/// </summary>
/// <remarks>Linked into Verbara.Platform.Storage.Postgres.Tests.</remarks>
public abstract class MessageHistoryStoreContract
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("t-history");
    private static readonly TenantId OtherTenant = new("t-history-other");

    /// <summary>A new, empty message store.</summary>
    protected abstract Task<IMessageStore> NewStoreAsync();

    private static EntityId NewConversationId() => EntityId.From($"c-{Guid.NewGuid():N}");

    private static async Task<Message> StoreAsync(
        IMessageStore store, EntityId conversationId, DateTimeOffset at, string? messageId = null,
        TenantId? tenant = null, MessageDirection direction = MessageDirection.Inbound)
    {
        var message = new Message
        {
            MessageId = EntityId.From(messageId ?? $"m-{Guid.NewGuid():N}"),
            ConversationId = conversationId,
            TenantId = tenant ?? Tenant,
            Direction = direction,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock($"at {at:O}")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = at,
        };
        await store.SaveAsync(message, CancellationToken.None);
        return message;
    }

    private static MessageHistoryCursor CursorOf(Message last) => new(last.CreatedAt, last.MessageId);

    private static Task<MessageHistorySlice> ReadAsync(
        IMessageStore store, EntityId conversationId, int limit, MessageHistoryCursor? before = null) =>
        store.GetConversationHistoryAsync(Tenant, conversationId, limit, before, CancellationToken.None);

    /// <summary>Every page from the newest, following each page's last item, until no older message exists.</summary>
    private static async Task<List<string>> WalkAsync(IMessageStore store, EntityId conversationId, int limit)
    {
        var ids = new List<string>();
        MessageHistoryCursor? before = null;
        for (var pages = 0; pages < 1000; pages++)
        {
            var slice = await ReadAsync(store, conversationId, limit, before);
            ids.AddRange(slice.Items.Select(m => m.MessageId.Value));
            if (!slice.HasMore)
                return ids;
            slice.Items.Should().HaveCount(limit, "a page with an older message after it is full");
            before = CursorOf(slice.Items[^1]);
        }

        throw new InvalidOperationException("the history never ended");
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldAnswerTheNewestFirstAndFlagMore_WhenMoreMessagesThanTheLimitExist()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var stored = new List<Message>();
        for (var i = 0; i < 5; i++)
            stored.Add(await StoreAsync(store, conversation, T0.AddMinutes(i)));

        var three = await ReadAsync(store, conversation, 3);
        var five = await ReadAsync(store, conversation, 5);
        var ten = await ReadAsync(store, conversation, 10);

        three.Items.Select(m => m.MessageId).Should().Equal(stored[4].MessageId, stored[3].MessageId, stored[2].MessageId);
        three.HasMore.Should().BeTrue("two older messages exist past the page");
        five.Items.Should().HaveCount(5);
        five.HasMore.Should().BeFalse("the extra row decides hasMore, and there is none");
        ten.Items.Select(m => m.MessageId).Should().Equal(stored.Select(m => m.MessageId).Reverse());
        ten.HasMore.Should().BeFalse();
        ten.Items[0].Content.Blocks.OfType<TextBlock>().Single().Text.Should().Be($"at {T0.AddMinutes(4):O}", "the message is read whole");
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldAnswerOnlyStrictlyOlderMessages_WhenACursorIsGiven()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var stored = new List<Message>();
        for (var i = 0; i < 5; i++)
            stored.Add(await StoreAsync(store, conversation, T0.AddMinutes(i)));

        var older = await ReadAsync(store, conversation, 2, CursorOf(stored[2]));
        var oldest = await ReadAsync(store, conversation, 2, CursorOf(stored[0]));

        older.Items.Select(m => m.MessageId).Should().Equal(stored[1].MessageId, stored[0].MessageId);
        older.HasMore.Should().BeFalse();
        oldest.Items.Should().BeEmpty("nothing is older than the oldest message");
        oldest.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldBreakEqualTimesByMessageIdDescendingOrdinal_WhenMessagesShareCreatedAt()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var prefix = Guid.NewGuid().ToString("N");
        // Ordinal: 'B' (0x42) < 'a' (0x61) < 'c' (0x63). A linguistic collation puts 'a' before 'B'.
        await StoreAsync(store, conversation, T0, $"{prefix}-a");
        await StoreAsync(store, conversation, T0, $"{prefix}-B");
        await StoreAsync(store, conversation, T0, $"{prefix}-c");
        await StoreAsync(store, conversation, T0.AddMinutes(-1), $"{prefix}-z");

        var all = await ReadAsync(store, conversation, 10);
        var walked = await WalkAsync(store, conversation, 1);

        string[] expected = [$"{prefix}-c", $"{prefix}-a", $"{prefix}-B", $"{prefix}-z"];
        all.Items.Select(m => m.MessageId.Value).Should().Equal(expected);
        walked.Should().Equal(expected, "a cursor inside a run of equal times continues by the message id, skipping none");
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldRepeatAndSkipNothing_WhenAMessageIsStoredBetweenTwoPages()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var stored = new List<Message>();
        for (var i = 0; i < 6; i++)
            stored.Add(await StoreAsync(store, conversation, T0.AddMinutes(i)));

        var first = await ReadAsync(store, conversation, 3);
        await StoreAsync(store, conversation, T0.AddMinutes(10));
        var second = await ReadAsync(store, conversation, 3, CursorOf(first.Items[^1]));

        first.Items.Concat(second.Items).Select(m => m.MessageId)
            .Should().Equal(stored.Select(m => m.MessageId).Reverse(), "the new message is newer than the cursor, not on its page");
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldReadOnlyTheConversationOfTheTenant_WhenOthersHoldMessages()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var other = NewConversationId();
        var mine = await StoreAsync(store, conversation, T0);
        await StoreAsync(store, other, T0.AddMinutes(1));
        await StoreAsync(store, conversation, T0.AddMinutes(2), tenant: OtherTenant);

        var slice = await ReadAsync(store, conversation, 10);
        var empty = await ReadAsync(store, NewConversationId(), 10);

        slice.Items.Select(m => m.MessageId).Should().Equal(mine.MessageId);
        slice.HasMore.Should().BeFalse();
        empty.Items.Should().BeEmpty("a conversation with no message has an empty history");
        empty.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldOnlyBoundTheKeyset_WhenTheCursorNamesAMessageOfAnotherConversation()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var other = NewConversationId();
        var older = await StoreAsync(store, conversation, T0);
        await StoreAsync(store, conversation, T0.AddMinutes(5));
        var foreign = await StoreAsync(store, other, T0.AddMinutes(3));
        await StoreAsync(store, other, T0.AddMinutes(1));

        var slice = await ReadAsync(store, conversation, 10, CursorOf(foreign));

        slice.Items.Select(m => m.MessageId).Should().Equal(older.MessageId);
        slice.Items.Should().OnlyContain(m => m.ConversationId == conversation, "the read is pinned to the conversation");
    }

    [Fact]
    public async Task GetConversationHistoryAsync_ShouldWalkEveryMessageOnce_WhenTimesCarrySubMicrosecondTicks()
    {
        var store = await NewStoreAsync();
        var conversation = NewConversationId();
        var stored = new List<Message>();
        for (var i = 0; i < 7; i++)
            stored.Add(await StoreAsync(store, conversation, T0.AddTicks(i * 3)));

        var walked = await WalkAsync(store, conversation, 2);

        walked.Should().BeEquivalentTo(stored.Select(m => m.MessageId.Value), "every message is read once");
        walked.Should().OnlyHaveUniqueItems();
    }
}
