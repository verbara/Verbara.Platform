#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup returns ValueTask

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.OutboundTrunk;
using Verbara.Platform.Api.Tests.Cluster;
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
/// Pro <c>2.29.0-pro</c>: the originate executor click-to-dial and callbacks use hands the half-open probe's slot to the
/// next dial when the probe's own caller cancels it, instead of holding the node for another open window. The executor
/// is the instance the real Api <c>Program</c> registers (a host with the voice AMI node configured, over a
/// Testcontainers Postgres for the cluster connection), resolved from its container with the host's
/// <see cref="TimeProvider"/> replaced by a fake one and its licence guard by one that allows (the test host carries
/// no licence), and the primary server added to the host's own pool. A pin of
/// Pro's red-first tests, not red-first here; the control in this class (a probe that fails keeps the node refused for
/// the window) is what shows the pin can fail.
/// </summary>
public sealed class ClickToDialProbeCancellationTests : IAsyncLifetime
{
    private const string Tenant = "acme";
    private const string PrimaryNode = "primary";
    private static readonly TenantId TenantId = new(Tenant);

    // DefaultOriginateExecutor's own constants: five consecutive failures open the circuit for thirty seconds.
    private const int FailureThreshold = 5;
    private static readonly TimeSpan PastTheOpenWindow = TimeSpan.FromSeconds(31);

    private readonly AdmissionPostgres _postgres = new();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly IContactIdentityResolver _contacts = Substitute.For<IContactIdentityResolver>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly IQueueStore _queues = Substitute.For<IQueueStore>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly EntityId _agentId = EntityId.New();
    private readonly EntityId _queueId = EntityId.New();
    private readonly Queue<Func<CancellationToken, Task<ManagerResponse>>> _answers = new();
    private WebApplicationFactory<Program>? _host;
    private OriginateExecutorBase? _executor;
    private int _originatesSent;

    public ClickToDialProbeCancellationTests()
    {
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
        _queues.GetByIdAsync(TenantId, _queueId, Arg.Any<CancellationToken>())
            .Returns(new Queue { QueueId = _queueId, TenantId = TenantId, Name = "support", CreatedAt = DateTimeOffset.UnixEpoch });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgres.StartAsync();
        _host = new PlatformApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Cluster", _postgres.ConnectionString);
            builder.UseSetting("Asterisk:Ami:Hostname", "primary.click-to-dial.invalid");
            builder.UseSetting("Asterisk:Ami:Port", "5038");
            builder.UseSetting("Asterisk:Ami:Username", "click-to-dial");
            builder.UseSetting("Asterisk:Ami:Password", "click-to-dial");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_time);
                services.RemoveAll<ILicenseGuard>();
                services.AddSingleton(AllowEverything());
            });
        });

        var services = _host.Services;
        _executor = services.GetRequiredService<OriginateExecutorBase>();
        services.GetRequiredService<VerbaraServerPool>()
            .AddExistingServer(PrimaryNode, new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        await _postgres.StopAsync();
    }

    [Fact]
    public void Host_ShouldRegisterOneExecutorForClickToDialAndCallbacks_WhenTheVoiceAmiNodeIsConfigured()
    {
        _executor.Should().BeOfType<DefaultOriginateExecutor>("Program.cs registers Pro's default originate executor");
        _host!.Services.GetRequiredService<OriginateExecutorBase>().Should().BeSameAs(_executor,
            "one singleton executor, so click-to-dial and callbacks share its per-node circuit");
        _host.Services.GetRequiredService<IAgentOutboundDialService>().Should().BeOfType<AgentOutboundDialService>();
        _host.Services.GetRequiredService<ICallbackOriginator>().Should().BeOfType<CallbackOriginator>();
    }

    [Fact]
    public async Task DialAsync_ShouldReachTheServer_WhenTheHalfOpenProbeWasCancelledByItsCaller()
    {
        var sut = CreateClickToDial();
        await TripPrimaryAsync(sut);
        _time.Advance(PastTheOpenWindow);

        var probeStarted = HoldNextOriginateUntilCancelled();
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
        var sut = CreateClickToDial();
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

    [Fact]
    public async Task OriginateCallbackAsync_ShouldHandTheProbeToTheNextDial_WhenTheCallbackHeldTheProbeAndWasCancelled()
    {
        // The callback originator shares the click-to-dial executor: the circuit a click-to-dial tripped is the one
        // the callback probes, and the slot a cancelled callback probe gives up goes to the next click-to-dial.
        var clickToDial = CreateClickToDial();
        var callbacks = CreateCallbackOriginator();
        await TripPrimaryAsync(clickToDial);
        (await callbacks.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, EntityId.New(), 1, CancellationToken.None))
            .Should().BeFalse("precondition: the callback shares the open circuit");
        _time.Advance(PastTheOpenWindow);

        var probeStarted = HoldNextOriginateUntilCancelled();
        using var cancel = new CancellationTokenSource();
        var probe = callbacks.OriginateCallbackAsync(TenantId, "+15551234567", _queueId, EntityId.New(), 1, cancel.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        (await probe).Should().BeFalse("precondition: the cancelled callback probe placed no call");
        var sentBefore = Volatile.Read(ref _originatesSent);

        var next = await clickToDial.DialAsync(TenantId, _agentId, "+15551234567", null, CancellationToken.None);

        Volatile.Read(ref _originatesSent).Should().Be(sentBefore + 1, "the next dial reaches the server's originate");
        next.Accepted.Should().BeTrue("the cancelled callback probe's slot went to the next dial, inside the same window");
    }

    private TaskCompletionSource HoldNextOriginateUntilCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _answers.Enqueue(async ct =>
        {
            started.TrySetResult();
            // The probe's originate is in flight until its caller cancels it.
            var inFlight = new TaskCompletionSource<ManagerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => inFlight.TrySetCanceled(ct)))
                return await inFlight.Task;
        });
        return started;
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

    private static DialableTrunkResolver Trunks() =>
        Resolver(null, new FakeTrunkStore().Add(Tenant, IpTrunk(7, "pstn-default", "198.51.100.7")), "pstn-default");

    /// <summary>Click-to-dial over the host's executor.</summary>
    private AgentOutboundDialService CreateClickToDial()
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(true);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new AgentOutboundDialService(
            Substitute.For<IConversationStore>(), Substitute.For<IAgentCapacityService>(), _contacts, new InMemoryContactStore(),
            _agents, _tenants, _executor!, Trunks(), null, leader, _clock,
            NullLogger<AgentOutboundDialService>.Instance);
    }

    /// <summary>The callback originator over the same host executor.</summary>
    private CallbackOriginator CreateCallbackOriginator() => new(
        Substitute.For<IConversationStore>(), _queues, _tenants, _contacts, _executor!, Trunks(), _clock,
        NullLogger<CallbackOriginator>.Instance);

    private static ILicenseGuard AllowEverything()
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(true, null));
        guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>())
            .Returns(new LicenseGuardResult(true, null));
        return guard;
    }
}
