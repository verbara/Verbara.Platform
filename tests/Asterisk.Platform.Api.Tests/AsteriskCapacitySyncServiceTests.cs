using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Queues.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class AsteriskCapacitySyncServiceTests : IDisposable
{
    private const string TestTenantId = "t001";
    private const string TestExtension = "1001";

    private readonly IAgentCapacityService _capacityService = Substitute.For<IAgentCapacityService>();
    private readonly IAgentStore _agentStore = Substitute.For<IAgentStore>();
    private readonly PlatformEventBus _eventBus = new();

    private readonly AsteriskCapacitySyncService _sut;

    public AsteriskCapacitySyncServiceTests()
    {
        _sut = new AsteriskCapacitySyncService(
            _capacityService,
            _agentStore,
            _eventBus,
            NullLogger<AsteriskCapacitySyncService>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _eventBus.Dispose();
    }

    private static Agent MakeAgent(string extension) => new()
    {
        AgentId = EntityId.From("a1"),
        TenantId = new TenantId(TestTenantId),
        UserId = EntityId.From("u1"),
        DisplayName = "Test Agent",
        State = AgentState.Available,
        Extension = extension,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task HandleVoiceCallStartedAsync_ShouldReserveCapacity_WhenAgentFound()
    {
        var agent = MakeAgent(TestExtension);
        _agentStore.GetByExtensionAsync(
                Arg.Any<TenantId>(), TestExtension, Arg.Any<CancellationToken>())
            .Returns(agent);

        await _sut.HandleVoiceCallStartedAsync(TestTenantId, TestExtension, CancellationToken.None);

        await _capacityService.Received(1).ReserveAsync(
            Arg.Is<TenantId>(t => t.Value == TestTenantId),
            agent.AgentId,
            ChannelType.Voice,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleVoiceCallEndedAsync_ShouldReleaseCapacity_WhenAgentFound()
    {
        var agent = MakeAgent(TestExtension);
        _agentStore.GetByExtensionAsync(
                Arg.Any<TenantId>(), TestExtension, Arg.Any<CancellationToken>())
            .Returns(agent);

        await _sut.HandleVoiceCallEndedAsync(TestTenantId, TestExtension, CancellationToken.None);

        await _capacityService.Received(1).ReleaseAsync(
            Arg.Is<TenantId>(t => t.Value == TestTenantId),
            agent.AgentId,
            ChannelType.Voice,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleVoiceCallStartedAsync_ShouldSkip_WhenAgentNotFound()
    {
        _agentStore.GetByExtensionAsync(
                Arg.Any<TenantId>(), TestExtension, Arg.Any<CancellationToken>())
            .Returns((Agent?)null);

        await _sut.HandleVoiceCallStartedAsync(TestTenantId, TestExtension, CancellationToken.None);

        await _capacityService.DidNotReceive().ReserveAsync(
            Arg.Any<TenantId>(),
            Arg.Any<EntityId>(),
            Arg.Any<ChannelType>(),
            Arg.Any<CancellationToken>());
    }
}
