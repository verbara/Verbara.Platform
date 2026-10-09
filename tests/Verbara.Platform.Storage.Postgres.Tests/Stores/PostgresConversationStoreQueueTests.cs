using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="PostgresConversationStore.ListQueuedAsync"/> feeds <c>QueueDistributionWorker</c>,
/// which can only route a conversation a queue owns. Conversations in <c>Queued</c> without a
/// queue owner must not occupy the bounded window, or they starve routable work.
/// Mirrors <c>InMemoryConversationStoreTests</c> so the two stores agree.
/// </summary>
public class PostgresConversationStoreQueueTests : IClassFixture<ConversationVoiceLinkFixture>, IAsyncLifetime
{
    private readonly ConversationVoiceLinkFixture _fixture;
    private readonly PostgresConversationStore _sut;
    private readonly TenantId _tenant;

    public PostgresConversationStoreQueueTests(ConversationVoiceLinkFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresConversationStore(_fixture.DataSource);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Conversation MakeQueued(DateTimeOffset createdAt, ConversationOwner? owner) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            Owner = owner,
            CreatedAt = createdAt,
        };

    [Fact]
    public async Task ListQueuedAsync_ShouldReturnOnlyQueueOwnedConversations_WhenOwnerlessQueuedFillTheWindow()
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 60; i++)
            await _sut.SaveAsync(MakeQueued(start.AddSeconds(i), owner: null), CancellationToken.None);
        await _sut.SaveAsync(
            MakeQueued(start.AddMinutes(5), ConversationOwner.ForBot(EntityId.New())), CancellationToken.None);
        var queueOwned = MakeQueued(start.AddMinutes(10), ConversationOwner.ForQueue(EntityId.New()));
        await _sut.SaveAsync(queueOwned, CancellationToken.None);

        var result = await _sut.ListQueuedAsync(_tenant, 50, CancellationToken.None);

        result.Select(c => c.ConversationId).Should().ContainSingle()
            .Which.Should().Be(queueOwned.ConversationId);
    }

    [Fact]
    public async Task ListQueuedAsync_ShouldKeepPriorityThenOldestFirstAndLimit_WhenAllQueueOwned()
    {
        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var queue = ConversationOwner.ForQueue(EntityId.New());
        var oldest = MakeQueued(start, queue);
        var middle = MakeQueued(start.AddMinutes(1), queue);
        var front = MakeQueued(start.AddMinutes(2), queue);
        front.QueuePriority = -1;
        var newest = MakeQueued(start.AddMinutes(3), queue);
        foreach (var c in new[] { newest, middle, front, oldest })
            await _sut.SaveAsync(c, CancellationToken.None);

        var result = await _sut.ListQueuedAsync(_tenant, 3, CancellationToken.None);

        result.Select(c => c.ConversationId).Should().Equal(
            front.ConversationId, oldest.ConversationId, middle.ConversationId);
    }
}
