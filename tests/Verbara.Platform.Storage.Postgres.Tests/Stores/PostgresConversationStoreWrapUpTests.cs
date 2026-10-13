using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="PostgresConversationStore.TryCommitWrapUpAsync"/> is the one way an agent-held digital
/// conversation enters <c>WrapUp</c> (close-chat-without-typification-licence D2/D3): a conditional
/// <c>UPDATE</c> on state AND owner, so exactly one caller wins and only the winner releases the
/// owner's capacity unit. Mirrors <c>InMemoryConversationStoreTests</c> so the two stores agree.
/// </summary>
public class PostgresConversationStoreWrapUpTests : IClassFixture<ConversationVoiceLinkFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 12, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly ConversationVoiceLinkFixture _fixture;
    private readonly PostgresConversationStore _sut;
    private readonly TenantId _tenant;

    public PostgresConversationStoreWrapUpTests(ConversationVoiceLinkFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresConversationStore(_fixture.DataSource);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Conversation MakeAgentHeld(EntityId ownerAgentId, ConversationState state) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = state,
            Owner = ConversationOwner.ForAgent(ownerAgentId),
            CreatedAt = Earlier.AddHours(-1),
            UpdatedAt = Earlier,
        };

    [Fact]
    public async Task TryCommitWrapUpAsync_ShouldCommit_WhenStateAndOwnerMatch()
    {
        var owner = EntityId.New();
        var conv = MakeAgentHeld(owner, ConversationState.Active);
        await _sut.SaveAsync(conv, CancellationToken.None);

        var won = await _sut.TryCommitWrapUpAsync(_tenant, conv.ConversationId, owner, Now, CancellationToken.None);

        won.Should().BeTrue("the row is still agent-held by this owner");
        var row = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);
        row!.State.Should().Be(ConversationState.WrapUp);
        row.UpdatedAt.Should().Be(Now);
        row.Owner.Should().Be(ConversationOwner.ForAgent(owner), "wrap-up keeps the owner");
    }

    [Fact]
    public async Task TryCommitWrapUpAsync_ShouldReturnFalseAndNotChangeRow_WhenStateMoved()
    {
        var owner = EntityId.New();
        var conv = MakeAgentHeld(owner, ConversationState.WrapUp);
        await _sut.SaveAsync(conv, CancellationToken.None);

        var won = await _sut.TryCommitWrapUpAsync(_tenant, conv.ConversationId, owner, Now, CancellationToken.None);

        won.Should().BeFalse("another caller already moved the row to WrapUp");
        var row = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);
        row!.State.Should().Be(ConversationState.WrapUp);
        row.UpdatedAt.Should().Be(Earlier, "a lost update writes nothing");
    }

    [Fact]
    public async Task TryCommitWrapUpAsync_ShouldReturnFalseAndNotChangeRow_WhenOwnerDiffers()
    {
        // A transfer keeps Active and swaps the owner: the old owner's wrap-up must not win.
        var oldOwner = EntityId.New();
        var newOwner = EntityId.New();
        var conv = MakeAgentHeld(newOwner, ConversationState.Active);
        await _sut.SaveAsync(conv, CancellationToken.None);

        var won = await _sut.TryCommitWrapUpAsync(_tenant, conv.ConversationId, oldOwner, Now, CancellationToken.None);

        won.Should().BeFalse("the row is no longer held by the owner the caller read");
        var row = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);
        row!.State.Should().Be(ConversationState.Active);
        row.Owner.Should().Be(ConversationOwner.ForAgent(newOwner));
        row.UpdatedAt.Should().Be(Earlier);
    }

    [Theory]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    [InlineData(ConversationState.Consulting)]
    public async Task TryCommitWrapUpAsync_ShouldMatchRowWrittenBySaveAsync(ConversationState agentHeld)
    {
        // The predicate binds owner_kind as an integer and owner_id as text: it must match exactly
        // what SaveAsync wrote for an agent owner, or every close would lose silently.
        var owner = EntityId.New();
        var conv = MakeAgentHeld(owner, agentHeld);
        await _sut.SaveAsync(conv, CancellationToken.None);

        var won = await _sut.TryCommitWrapUpAsync(_tenant, conv.ConversationId, owner, Now, CancellationToken.None);

        won.Should().BeTrue($"a row SaveAsync wrote in {agentHeld} with this agent owner must match the predicate");
        (await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None))!
            .State.Should().Be(ConversationState.WrapUp);
    }
}
