#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup returns ValueTask

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.OutboundTrunk;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using static Verbara.Platform.Api.Tests.OutboundTrunk.TrunkFakes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pro <c>2.29.0-pro</c>: the originate executor click-to-dial uses (built as <c>Program.cs</c> builds it) hands the
/// half-open probe's slot to the next dial when the probe's own caller cancels it, instead of holding the node for
/// another open window. A pin of Pro's red-first tests, not red-first here; the control in this class (a probe that
/// fails keeps the node refused for the window) is what shows the pin can fail.
/// </summary>
public sealed class ClickToDialProbeCancellationTests : IAsyncDisposable
{
    private const string Tenant = "acme";
    private const string PrimaryNode = "primary";
    private static readonly TenantId TenantId = new(Tenant);

    // DefaultOriginateExecutor's own constants: five consecutive failures open the circuit for thirty seconds.
    private const int FailureThreshold = 5;
    private static readonly TimeSpan PastTheOpenWindow = TimeSpan.FromSeconds(31);

    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _pool = new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly EntityId _agentId = EntityId.New();
    private readonly Queue<Func<CancellationToken, Task<ManagerResponse>>> _answers = new();
    private int _originatesSent;

    public ClickToDialProbeCancellationTests()
    {
        _pool.AddExistingServer(PrimaryNode, new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            Interlocked.Increment(ref _originatesSent);
            var answer = _answers.Count > 0 ? _answers.Dequeue() : Answer("Success");
            return new ValueTask<ManagerResponse>(answer(ci.ArgAt<CancellationToken>(1)));
        });
        _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        _agents.GetByIdAsync(TenantId, _agentId, Arg.Any<CancellationToken>()).Returns(new Agent
        {
            AgentId = _agentId, TenantId = TenantId, UserId = EntityId.New(),
            DisplayName = "A", State = AgentState.Available, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _contacts.ResolveAsync(TenantId, Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = EntityId.New(), TenantId = TenantId, CreatedAt = DateTimeOffset.UnixEpoch });
        _tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "Acme", Options = new TenantOptions { OutboundCallerId = "+15558675309" } });
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    [Fact]
    public async Task DialAsync_ShouldReachTheServer_WhenTheHalfOpenProbeWasCancelledByItsCaller()
    {
        var sut = CreateService();
        await TripPrimaryAsync(sut);
        _time.Advance(PastTheOpenWindow);

        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _answers.Enqueue(async ct =>
        {
            probeStarted.TrySetResult();
            // The probe's originate is in flight until its caller cancels it.
            var inFlight = new TaskCompletionSource<ManagerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => inFlight.TrySetCanceled(ct)))
                return await inFlight.Task;
        });
        using var cancel = new CancellationTokenSource();
        var probe = sut.DialAsync(TenantId, _agentId, "+15551234567", null, cancel.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        (await probe).Accepted.Should().BeFalse("precondition: the cancelled probe placed no call");
        var sentBefore = Volatile.Read(ref _originatesSent);

        var next = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        Volatile.Read(ref _originatesSent).Should().Be(sentBefore + 1, "the next dial reaches the server's originate");
        next.Accepted.Should().BeTrue("the cancelled probe's slot went to the next dial, inside the same window");
    }

    [Fact]
    public async Task DialAsync_ShouldBeRefusedInsideTheWindow_WhenTheHalfOpenProbeFailed()
    {
        var sut = CreateService();
        await TripPrimaryAsync(sut);
        _time.Advance(PastTheOpenWindow);
        _answers.Enqueue(Answer("Error"));
        (await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None))
            .Accepted.Should().BeFalse("precondition: the probe failed");
        var sentBefore = Volatile.Read(ref _originatesSent);

        var next = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        next.Accepted.Should().BeFalse("a failed probe opens the circuit again");
        next.Error.Should().Be("Circuit breaker open");
        Volatile.Read(ref _originatesSent).Should().Be(sentBefore, "the refused dial never reaches the server");
    }

    private async Task TripPrimaryAsync(AgentOutboundDialService sut)
    {
        for (var i = 0; i < FailureThreshold; i++)
        {
            _answers.Enqueue(Answer("Error"));
            (await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None)).Accepted.Should().BeFalse();
        }

        var refused = await sut.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);
        refused.Error.Should().Be("Circuit breaker open", "precondition: five failures opened primary's circuit");
    }

    private static Func<CancellationToken, Task<ManagerResponse>> Answer(string response) =>
        _ => Task.FromResult(new ManagerResponse { Response = response, Message = response == "Success" ? null : "Originate failed" });

    /// <summary>Click-to-dial over the executor built as <c>Program.cs</c> builds it (a licence guard that allows).</summary>
    private AgentOutboundDialService CreateService()
    {
        var executor = new DefaultOriginateExecutor(
            _pool, NullLogger<DefaultOriginateExecutor>.Instance, null, null, _time, AllowEverything());
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(true);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        var trunks = new FakeTrunkStore().Add(Tenant, IpTrunk(7, "pstn-default", "198.51.100.7"));
        return new AgentOutboundDialService(
            Substitute.For<IConversationStore>(), Substitute.For<IAgentCapacityService>(), _contacts, new InMemoryContactStore(),
            _agents, _tenants, executor, Resolver(null, trunks, "pstn-default"), null, leader, _clock,
            NullLogger<AgentOutboundDialService>.Instance);
    }

    private static ILicenseGuard AllowEverything()
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(true, null));
        guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>())
            .Returns(new LicenseGuardResult(true, null));
        return guard;
    }
}
