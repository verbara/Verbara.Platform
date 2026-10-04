using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Realtime;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// queue-members-stay-unpaused-across-reconcile — the shared pause rule, voice gate and per-agent
/// lock of <see cref="AgentPauseCoordinator"/> (design D2).
/// </summary>
public sealed class AgentPauseCoordinatorTests
{
    private const string Tenant = "t-coord";

    [Theory]
    [InlineData(AgentState.Available, false)]
    [InlineData(AgentState.Busy, false)]
    [InlineData(AgentState.Break, true)]
    [InlineData(AgentState.Lunch, true)]
    [InlineData(AgentState.Training, true)]
    [InlineData(AgentState.ACW, true)]
    [InlineData(AgentState.DND, true)]
    [InlineData(AgentState.Offline, true)]
    public async Task ConvergeAsync_ShouldWriteRulePausedValue_ForEachState(AgentState state, bool expectedPaused)
    {
        var (agents, agent) = await SeedAsync(state);
        var sync = Substitute.For<IRealtimeSyncService>();

        var written = await new AgentPauseCoordinator().ConvergeAsync(
            agent.TenantId, agent.AgentId, agents, sync, CancellationToken.None);

        written.Should().Be(expectedPaused);
        await sync.Received(1).SyncAgentPausedAsync(Tenant, agent.AgentId.Value, expectedPaused, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConvergeAsync_ShouldPause_WhenAvailableAgentHasPendingPause()
    {
        var (agents, agent) = await SeedAsync(AgentState.Available, pending: AgentState.Break);
        var sync = Substitute.For<IRealtimeSyncService>();

        var written = await new AgentPauseCoordinator().ConvergeAsync(
            agent.TenantId, agent.AgentId, agents, sync, CancellationToken.None);

        written.Should().BeTrue();
        await sync.Received(1).SyncAgentPausedAsync(Tenant, agent.AgentId.Value, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConvergeAsync_ShouldWriteNothing_WhenAgentIsMissing()
    {
        var sync = Substitute.For<IRealtimeSyncService>();

        var written = await new AgentPauseCoordinator().ConvergeAsync(
            new TenantId(Tenant), EntityId.New(), new InMemoryAgentStore(), sync, CancellationToken.None);

        written.Should().BeNull();
        await sync.DidNotReceiveWithAnyArgs().SyncAgentPausedAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ConvergeAsync_ShouldReadTheAgentOnlyAfterTheLockIsFree()
    {
        // The read happens UNDER the lock: a writer holding it (the bridge) finishes first, so the
        // convergence sees the state that writer published, never a value read before it.
        var (agents, agent) = await SeedAsync(AgentState.Break);
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new AgentPauseCoordinator();

        var held = await sut.AcquireAsync(agent.AgentId.Value, CancellationToken.None);
        var converge = sut.ConvergeAsync(agent.TenantId, agent.AgentId, agents, sync, CancellationToken.None);
        // No wall-clock wait: a contended SemaphoreSlim.WaitAsync returns an incomplete task synchronously.
        converge.IsCompleted.Should().BeFalse("the convergence must wait for the agent's lock");

        // The holder's state change lands before release, as a NEW row (a read taken before the
        // lock would still hold the old Break instance).
        await agents.SaveAsync(CopyWithState(agent, AgentState.Available), CancellationToken.None);
        held.Dispose();

        (await converge.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        await sync.Received(1).SyncAgentPausedAsync(Tenant, agent.AgentId.Value, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireAsync_ShouldNotBlockOtherAgents()
    {
        var sut = new AgentPauseCoordinator();

        using var held = await sut.AcquireAsync("a1", CancellationToken.None);
        var other = sut.AcquireAsync("a2", CancellationToken.None);

        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ShouldReleaseOnce_WhenHandleIsDisposedTwice()
    {
        var sut = new AgentPauseCoordinator();
        var first = await sut.AcquireAsync("a1", CancellationToken.None);
        first.Dispose();
        first.Dispose();

        using var second = await sut.AcquireAsync("a1", CancellationToken.None);
        var third = sut.AcquireAsync("a1", CancellationToken.None);
        // No wall-clock wait: a contended SemaphoreSlim.WaitAsync returns an incomplete task synchronously.
        third.IsCompleted.Should().BeFalse("a double dispose must not over-release the semaphore");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new[] { "voice" }, true)]
    [InlineData(new[] { "WebChat", "Voice" }, true)]
    [InlineData(new[] { "WebChat" }, false)]
    [InlineData(new string[0], false)]
    public void CreatesVoiceRow_ShouldMirrorTheSdkVoiceGate(string[]? channels, bool expected)
        => AgentPauseCoordinator.CreatesVoiceRow(channels).Should().Be(expected);

    private static Agent CopyWithState(Agent agent, AgentState state) => new()
    {
        AgentId = agent.AgentId,
        TenantId = agent.TenantId,
        UserId = agent.UserId,
        DisplayName = agent.DisplayName,
        State = state,
        CreatedAt = agent.CreatedAt,
    };

    private static async Task<(InMemoryAgentStore Agents, Agent Agent)> SeedAsync(
        AgentState state, AgentState? pending = null)
    {
        var agents = new InMemoryAgentStore();
        var agent = new Agent
        {
            AgentId = EntityId.New(),
            TenantId = new TenantId(Tenant),
            UserId = EntityId.New(),
            DisplayName = "Coordinator Agent",
            State = state,
            PendingState = pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await agents.SaveAsync(agent, CancellationToken.None);
        return (agents, agent);
    }
}
