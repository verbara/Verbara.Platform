using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Verifies the Phase 3B.0 per-call voice idempotency contract against a real Postgres:
/// the <c>voice_linked_id</c> column round-trips and the partial unique index makes a
/// second conversation for the same physical call a no-op (the failover safety net behind
/// the leader-emit gate). Mirrors <c>InMemoryConversationStoreTests</c> so the two stores
/// agree (Phase-2.1 parity lesson).
/// </summary>
public class PostgresConversationStoreVoiceLinkTests : IClassFixture<ConversationVoiceLinkFixture>, IAsyncLifetime
{
    private readonly ConversationVoiceLinkFixture _fixture;
    private readonly PostgresConversationStore _sut;
    private readonly TenantId _tenant;

    public PostgresConversationStoreVoiceLinkTests(ConversationVoiceLinkFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresConversationStore(_fixture.DataSource);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Conversation MakeVoiceConversation(string linkedId, ConversationState state = ConversationState.Queued) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = linkedId,
        };

    [Fact]
    public async Task SaveAsync_ShouldRoundtripVoiceLinkedId_WhenVoiceConversation()
    {
        var conv = MakeVoiceConversation("linked-roundtrip");
        await _sut.SaveAsync(conv, CancellationToken.None);

        var retrieved = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);

        retrieved.Should().NotBeNull();
        retrieved!.VoiceLinkedId.Should().Be("linked-roundtrip");
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAsync_ShouldReturnConversation_WhenLinkedIdMatches()
    {
        var conv = MakeVoiceConversation("linked-find");
        await _sut.SaveAsync(conv, CancellationToken.None);

        var result = await _sut.FindByVoiceLinkedIdAsync(_tenant, "linked-find", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ConversationId.Should().Be(conv.ConversationId);
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAsync_ShouldReturnNull_WhenNoMatch()
    {
        await _sut.SaveAsync(MakeVoiceConversation("linked-find"), CancellationToken.None);

        var result = await _sut.FindByVoiceLinkedIdAsync(_tenant, "linked-absent", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldNoOp_WhenDifferentConversationHasSameVoiceLinkedId()
    {
        var first = MakeVoiceConversation("linked-dup");
        var duplicate = MakeVoiceConversation("linked-dup");

        await _sut.SaveAsync(first, CancellationToken.None);

        // The unique-violation must be swallowed as already-tracked (no throw).
        var act = async () => await _sut.SaveAsync(duplicate, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var byFirstId = await _sut.GetByIdAsync(_tenant, first.ConversationId, CancellationToken.None);
        var byDuplicateId = await _sut.GetByIdAsync(_tenant, duplicate.ConversationId, CancellationToken.None);
        var byLinkedId = await _sut.FindByVoiceLinkedIdAsync(_tenant, "linked-dup", CancellationToken.None);

        byFirstId.Should().NotBeNull();
        byDuplicateId.Should().BeNull("the second conversation with the same voice LinkedId is a no-op");
        byLinkedId!.ConversationId.Should().Be(first.ConversationId);
    }

    [Fact]
    public async Task FindByVoiceLinkedIdAcrossTenantsAsync_ShouldFindRegardlessOfTenant()
    {
        var conv = MakeVoiceConversation("linked-crosstenant");
        await _sut.SaveAsync(conv, CancellationToken.None);

        var result = await _sut.FindByVoiceLinkedIdAcrossTenantsAsync("linked-crosstenant", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ConversationId.Should().Be(conv.ConversationId);
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsert_WhenSameVoiceConversationResaved()
    {
        var conv = MakeVoiceConversation("linked-upsert", ConversationState.Queued);
        await _sut.SaveAsync(conv, CancellationToken.None);

        conv.State = ConversationState.Offered;
        await _sut.SaveAsync(conv, CancellationToken.None);

        var retrieved = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);
        retrieved!.State.Should().Be(ConversationState.Offered);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistVoiceLinkedId_WhenStampedAfterCreation()
    {
        // 3B.2d outbound: the dial service pre-creates the Conversation with a NULL voice_linked_id
        // (no live call yet); the bridge stamps the real LinkedId on CallConnected. The stamp lands
        // via the upsert's UPDATE branch, so it must persist (the bug: voice_linked_id was missing
        // from the DO UPDATE SET → the later stamp was silently dropped).
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            // VoiceLinkedId intentionally null at creation (outbound pre-create).
        };
        await _sut.SaveAsync(conv, CancellationToken.None);

        conv.VoiceLinkedId = "linked-stamped-late";
        await _sut.SaveAsync(conv, CancellationToken.None);

        var byId = await _sut.GetByIdAsync(_tenant, conv.ConversationId, CancellationToken.None);
        var byLinkedId = await _sut.FindByVoiceLinkedIdAsync(_tenant, "linked-stamped-late", CancellationToken.None);

        byId!.VoiceLinkedId.Should().Be("linked-stamped-late");
        byLinkedId.Should().NotBeNull("a LinkedId stamped after creation must be findable");
        byLinkedId!.ConversationId.Should().Be(conv.ConversationId);
    }

    [Fact]
    public async Task SaveAsync_ShouldNotConstrainNullVoiceLinkedId()
    {
        var a = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var b = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = _tenant,
            ContactId = EntityId.New(),
            Channel = ChannelType.WebChat,
            State = ConversationState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _sut.SaveAsync(a, CancellationToken.None);
        var act = async () => await _sut.SaveAsync(b, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await _sut.GetByIdAsync(_tenant, a.ConversationId, CancellationToken.None)).Should().NotBeNull();
        (await _sut.GetByIdAsync(_tenant, b.ConversationId, CancellationToken.None)).Should().NotBeNull();
    }
}
