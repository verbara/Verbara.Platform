using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues.Services;
using NSubstitute;
using Xunit;
using FluentAssertions;

namespace Asterisk.Platform.Queues.Tests.Services;

public class AgentCapacityServiceTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId AgentId = EntityId.From("a-001");

    private static Agent MakeAgent(int maxVoice = 1) => new()
    {
        AgentId = AgentId,
        TenantId = Tenant,
        UserId = EntityId.From("u-001"),
        DisplayName = "Agent",
        State = AgentState.Available,
        Capacity = new ChannelCapacity { MaxVoice = maxVoice },
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static (InMemoryAgentCapacityService Sut, IAgentStore Store) CreateSut(Agent? agent = null)
    {
        var store = Substitute.For<IAgentStore>();
        store.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
             .Returns(agent ?? MakeAgent());
        return (new InMemoryAgentCapacityService(store), store);
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnTrue_WhenUnderLimit()
    {
        var (sut, _) = CreateSut(MakeAgent(maxVoice: 2));

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnFalse_WhenAtLimit()
    {
        var (sut, _) = CreateSut(MakeAgent(maxVoice: 1));
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Reserve_ShouldIncrementLoad()
    {
        var (sut, _) = CreateSut();
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(1);
    }

    [Fact]
    public async Task Release_ShouldDecrementLoad()
    {
        var (sut, _) = CreateSut();
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        await sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(1);
    }

    [Fact]
    public async Task Release_ShouldNotGoBelowZero()
    {
        var (sut, _) = CreateSut();
        await sut.ReleaseAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(0);
    }

    [Fact]
    public async Task GetCurrentLoad_ShouldReturnZero_WhenNoReservations()
    {
        var (sut, _) = CreateSut();

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        load.Should().Be(0);
    }

    [Fact]
    public async Task HasCapacity_ShouldReturnFalse_WhenAgentNotFound()
    {
        var store = Substitute.For<IAgentStore>();
        store.GetByIdAsync(Tenant, AgentId, Arg.Any<CancellationToken>())
             .Returns((Agent?)null);
        var sut = new InMemoryAgentCapacityService(store);

        var result = await sut.HasCapacityAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Reserve_ShouldHandleConcurrentCalls()
    {
        var (sut, _) = CreateSut(MakeAgent(maxVoice: 10));
        var tasks = new List<Task>();

        for (var i = 0; i < 10; i++)
            tasks.Add(sut.ReserveAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None));

        await Task.WhenAll(tasks);

        var load = await sut.GetCurrentLoadAsync(Tenant, AgentId, ChannelType.Voice, CancellationToken.None);
        load.Should().Be(10);
    }
}
