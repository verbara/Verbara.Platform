using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Verbara.Platform.Queues.Tests.Services;

public class PersistentAgentCapacityServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");

    private static Agent MakeAgent() => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = AgentState.Available,
        CapacityOverride = new ChannelCapacityOverride(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (PersistentAgentCapacityService Sut, IAgentCapacityStore Store, IConversationStore ConvStore, IAgentStore AgentStore) CreateSut(
        ChannelCapacity? effective = null,
        Agent? agent = null,
        IReadOnlyList<Conversation>? activeConversations = null)
    {
        var agentStore = Substitute.For<IAgentStore>();
        agentStore.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
            .Returns(agent ?? MakeAgent());

        var resolver = Substitute.For<IAgentCapacityResolver>();
        resolver.ResolveAsync(Tenant, Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(effective ?? new ChannelCapacity());

        var capacityStore = Substitute.For<IAgentCapacityStore>();
        capacityStore.ListByTenantAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentCapacityRecord>>([]));

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.ListByStateAsync(Tenant, ConversationState.Active, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Conversation>>(activeConversations ?? []));

        var sut = new PersistentAgentCapacityService(agentStore, resolver, capacityStore, conversationStore);
        return (sut, capacityStore, conversationStore, agentStore);
    }

    private static Conversation MakeActiveConversation(ChannelType channel, string suffix) => new()
    {
        ConversationId = EntityId.From($"c-{channel}-{suffix}"),
        TenantId = Tenant,
        ContactId = EntityId.From($"contact-{suffix}"),
        Channel = channel,
        Owner = ConversationOwner.ForAgent(AgentId),
        State = ConversationState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ReserveAsync_ShouldPersistToStore()
    {
        var (sut, store, _, _) = CreateSut();

        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        await store.Received(1).UpsertAsync(
            Arg.Is<AgentCapacityRecord>(r =>
                r.TenantId == Tenant.Value &&
                r.AgentId == AgentId.Value &&
                r.VoiceLoad == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseAsync_ShouldPersistToStore()
    {
        var (sut, store, _, _) = CreateSut();

        // Reserve first so there's something to release
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        store.ClearReceivedCalls();

        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        await store.Received(1).UpsertAsync(
            Arg.Is<AgentCapacityRecord>(r =>
                r.TenantId == Tenant.Value &&
                r.AgentId == AgentId.Value &&
                r.VoiceLoad == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasCapacityAsync_ShouldDelegateCorrectly()
    {
        var (sut, _, _, _) = CreateSut(new ChannelCapacity { MaxVoice = 1 });

        var hasBefore = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        hasBefore.Should().BeTrue();

        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var hasAfter = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        hasAfter.Should().BeFalse();
    }

    // ─── W6-A4 — pooled chat persistence + reconcile pooling ─────────────────────

    [Fact]
    public async Task PersistCurrentLoadsAsync_ShouldPersistPooledChatLoad_WhenNonWebChatActive()
    {
        var (sut, store, _, _) = CreateSut(new ChannelCapacity { MaxChat = 5, MaxTotal = 10 });

        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);
        store.ClearReceivedCalls();

        await sut.ReserveAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);

        // The persisted record's ChatLoad reflects the POOLED chat total (2), not 0 — even though
        // the work is WhatsApp-typed, the inner pools it onto the canonical WebChat bucket.
        await store.Received(1).UpsertAsync(
            Arg.Is<AgentCapacityRecord>(r =>
                r.TenantId == Tenant.Value &&
                r.AgentId == AgentId.Value &&
                r.ChatLoad == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ShouldPoolChatFamily_WhenMixedActiveConversations()
    {
        var active = new List<Conversation>
        {
            MakeActiveConversation(ChannelType.WhatsApp, "1"),
            MakeActiveConversation(ChannelType.Messenger, "2"),
            MakeActiveConversation(ChannelType.WebChat, "3"),
        };

        var (sut, _, _, _) = CreateSut(
            new ChannelCapacity { MaxChat = 3, MaxTotal = 5 },
            activeConversations: active);

        // First call triggers reconcile: 3 mixed chat-family conversations pool into the single
        // WebChat bucket → MaxChat=3 is now full, so the next chat is rejected.
        var hasCapacity = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.WhatsApp, CancellationToken.None);

        hasCapacity.Should().BeFalse();

        // The pooled count is exactly 3 (not split per raw channel).
        var pooled = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.WebChat, CancellationToken.None);
        pooled.Should().Be(3);
    }
}
