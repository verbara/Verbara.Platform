using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Verbara.Platform.Queues.Tests.Services;

public class AgentCapacityResolverTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");

    private static Agent MakeAgent(ChannelCapacityOverride? over = null) => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = AgentState.Available,
        CapacityOverride = over ?? new ChannelCapacityOverride(),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (AgentCapacityResolver Sut, IAgentStore AgentStore, ICapacityDefaultsProvider Defaults) CreateSut(
        Agent? agent,
        ChannelCapacity defaults)
    {
        var agentStore = Substitute.For<IAgentStore>();
        agentStore.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(agent));

        var defaultsProvider = Substitute.For<ICapacityDefaultsProvider>();
        defaultsProvider.GetDefaultsAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(defaults));

        var sut = new AgentCapacityResolver(agentStore, defaultsProvider);
        return (sut, agentStore, defaultsProvider);
    }

    [Fact]
    public async Task ResolveAsync_ShouldInheritTenantDefault_WhenOverrideNull()
    {
        var defaults = new ChannelCapacity { MaxVoice = 1, MaxChat = 4, MaxEmail = 6, MaxSms = 2, MaxTotal = 7 };
        var (sut, _, _) = CreateSut(MakeAgent(new ChannelCapacityOverride()), defaults);

        var effective = await sut.ResolveAsync(Tenant, AgentId, CancellationToken.None);

        effective.Should().NotBeNull();
        effective!.MaxVoice.Should().Be(1);
        effective.MaxChat.Should().Be(4);
        effective.MaxEmail.Should().Be(6);
        effective.MaxSms.Should().Be(2);
        effective.MaxTotal.Should().Be(7);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseOverride_WhenOverrideSet()
    {
        var defaults = new ChannelCapacity { MaxVoice = 1, MaxChat = 3, MaxEmail = 5, MaxSms = 3, MaxTotal = 5 };
        var over = new ChannelCapacityOverride { MaxChat = 5 };
        var (sut, _, _) = CreateSut(MakeAgent(over), defaults);

        var effective = await sut.ResolveAsync(Tenant, AgentId, CancellationToken.None);

        effective.Should().NotBeNull();
        effective!.MaxChat.Should().Be(5);
        effective.MaxEmail.Should().Be(5);
        effective.MaxSms.Should().Be(3);
        effective.MaxTotal.Should().Be(5);
        effective.MaxVoice.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPinMaxVoiceToOne_WhenOverrideOrDefaultExceedsOne()
    {
        var defaults = new ChannelCapacity { MaxVoice = 9, MaxChat = 3, MaxEmail = 5, MaxSms = 3, MaxTotal = 5 };
        var over = new ChannelCapacityOverride { MaxVoice = 4 };
        var (sut, _, _) = CreateSut(MakeAgent(over), defaults);

        var effective = await sut.ResolveAsync(Tenant, AgentId, CancellationToken.None);

        effective.Should().NotBeNull();
        effective!.MaxVoice.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnNull_WhenAgentNotFound()
    {
        var defaults = new ChannelCapacity { MaxVoice = 9, MaxChat = 4, MaxEmail = 6, MaxSms = 2, MaxTotal = 7 };
        var (sut, _, _) = CreateSut(agent: null, defaults);

        var effective = await sut.ResolveAsync(Tenant, AgentId, CancellationToken.None);

        // A missing agent resolves to null (the resolver doubles as the existence signal) — it
        // does NOT merge defaults for a phantom agent.
        effective.Should().BeNull();
    }
}
