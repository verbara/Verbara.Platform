using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Verbara.Platform.Queues.Tests.Services;

public class PersistentAgentCapacityServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");

    private static Agent MakeAgent(int maxVoice = 5, int maxChat = 5, int maxEmail = 5) => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = AgentState.Available,
        // W6 — set the per-agent override so the effective per-channel maxima are the
        // test's chosen values (the service merges this over the class defaults via ToEffective).
        CapacityOverride = new ChannelCapacityOverride { MaxVoice = maxVoice, MaxChat = maxChat, MaxEmail = maxEmail },
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (PersistentAgentCapacityService Sut, IAgentCapacityStore Store, IConversationStore ConvStore, IAgentStore AgentStore) CreateSut(Agent? agent = null)
    {
        var agentStore = Substitute.For<IAgentStore>();
        agentStore.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
            .Returns(agent ?? MakeAgent());

        var capacityStore = Substitute.For<IAgentCapacityStore>();
        capacityStore.ListByTenantAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentCapacityRecord>>([]));

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.ListByStateAsync(Tenant, ConversationState.Active, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Conversation>>([]));

        var sut = new PersistentAgentCapacityService(agentStore, capacityStore, conversationStore);
        return (sut, capacityStore, conversationStore, agentStore);
    }

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
        var (sut, _, _, _) = CreateSut(MakeAgent(maxVoice: 1));

        var hasBefore = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        hasBefore.Should().BeTrue();

        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var hasAfter = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        hasAfter.Should().BeFalse();
    }
}
