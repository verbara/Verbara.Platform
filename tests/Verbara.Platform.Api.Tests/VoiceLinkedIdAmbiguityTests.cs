using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Hosting;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// From Sdk 2.10.x the session manager keeps its linked-id index per server, and the unscoped
/// <see cref="ICallSessionManager.GetByLinkedId(string)"/> answers only an id one server holds: <see langword="null"/>
/// when two or more do, where it returned whichever call last wrote the shared index. Platform's two callers that hold
/// only a linked id keep that lookup and fail closed on such an id: the event store's tenant resolver answers no tenant
/// (and logs the linked id), and a supervisor listen finds no agent leg. These run on the real Sdk manager with two
/// <see cref="VerbaraServer"/>s on substituted AMI connections attached to it, as <c>QueueWrapUpEngineRig</c> builds one.
/// </summary>
public sealed class VoiceLinkedIdAmbiguityTests : IAsyncDisposable
{
    private const string ServerA = "pod-a";
    private const string ServerB = "pod-b";

    /// <summary>The id both servers issue in the same second (two Asterisks without their own <c>systemname</c>).</summary>
    private const string SharedLinkedId = "1762430400.501";

    /// <summary>An id only <see cref="ServerA"/> holds.</summary>
    private const string OwnLinkedId = "1762430400.502";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private readonly ServiceProvider _services;
    private readonly CallSessionManager _manager;
    private readonly List<VerbaraServer> _servers = [];
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly RecordingLogger<VoiceCallTenantResolver> _resolverLog = new();

    public VoiceLinkedIdAmbiguityTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerbaraSessionsMultiServer();
        _services = services.BuildServiceProvider();
        _manager = (CallSessionManager)_services.GetRequiredService<ICallSessionManager>();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        await _services.DisposeAsync();
        await _serverPool.DisposeAsync();
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldAnswerNoTenant_WhenTwoAttachedServersHoldTheLinkedId()
    {
        await BothServersHoldTheSharedIdAsync();

        var tenant = await CreateResolver().ResolveTenantIdAsync(SharedLinkedId);

        tenant.Should().BeNull("an id two servers hold is no source: the resolver must not guess either server's call");
        _resolverLog.Messages.Should().ContainSingle(m => m.Contains(SharedLinkedId, StringComparison.Ordinal)
            && m.Contains("no live call session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetParticipants_ShouldAnswerNoLeg_WhenTwoAttachedServersHoldTheLinkedId()
    {
        await BothServersHoldTheSharedIdAsync();

        var legs = new CallSessionLegs(_manager).GetParticipants(SharedLinkedId);

        legs.Should().BeNull("a listen on an id two servers hold resolves no agent leg, so it originates no listen leg");
    }

    [Fact]
    public async Task BothSites_ShouldFindTheCall_WhenOneAttachedServerHoldsTheLinkedId()
    {
        await BothServersHoldTheSharedIdAsync();
        var own = await CallOfAsync(ServerA, OwnLinkedId, "PJSIP/trunk-a-00000502");
        own.TenantId = "acme";

        var tenant = await CreateResolver().ResolveTenantIdAsync(OwnLinkedId);
        var legs = new CallSessionLegs(_manager).GetParticipants(OwnLinkedId);

        tenant.Should().Be("acme", "the one server holding the id answers with its call");
        legs.Should().NotBeNull().And.NotBeEmpty();
    }

    private VoiceCallTenantResolver CreateResolver() => new(_conversations, _manager, _serverPool, _resolverLog);

    /// <summary>
    /// Each server reports a channel with <see cref="SharedLinkedId"/>; each call is stamped with a different tenant, so
    /// any fallback that picked one of them would answer a tenant.
    /// </summary>
    private async Task BothServersHoldTheSharedIdAsync()
    {
        var onA = await CallOfAsync(ServerA, SharedLinkedId, "PJSIP/trunk-a-00000501");
        var onB = await CallOfAsync(ServerB, SharedLinkedId, "PJSIP/trunk-b-00000501");
        onA.TenantId = "acme";
        onB.TenantId = "beta";

        onA.Should().NotBeSameAs(onB, "the same linked id on two servers is two calls");
        _manager.GetByLinkedId(SharedLinkedId).Should().BeNull("precondition: the unscoped lookup is ambiguous");
    }

    /// <summary>Delivers a trunk channel with <paramref name="linkedId"/> on <paramref name="serverId"/> and returns the
    /// call the manager opened for it, once its <see cref="CallStartedEvent"/> is raised.</summary>
    private async Task<CallSession> CallOfAsync(string serverId, string linkedId, string channel)
    {
        var frames = await ServerAsync(serverId);
        var started = new TaskCompletionSource<CallSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _manager.Events.Subscribe(e =>
        {
            if (e is CallStartedEvent s && s.ServerId == serverId
                && _manager.GetById(s.SessionId) is { } call && call.LinkedId == linkedId)
            {
                started.TrySetResult(call);
            }
        });

        frames.OnNext(new NewChannelEvent
        {
            EventType = "Newchannel", UniqueId = linkedId, Channel = channel, ChannelState = "Up",
            CallerIdNum = "5551234", Context = "from-trunk", Exten = "500", Priority = 1, Linkedid = linkedId,
            RawFields = new Dictionary<string, string>
            {
                ["Channel"] = channel, ["ChannelStateDesc"] = "Up", ["Uniqueid"] = linkedId, ["Linkedid"] = linkedId,
                ["CallerIDNum"] = "5551234",
            },
        });

        var opened = await started.Task.WaitAsync(Bound);
        _manager.GetByLinkedId(serverId, linkedId).Should().BeSameAs(opened, "the server's own index holds its call");
        return opened;
    }

    private readonly Dictionary<string, IObserver<ManagerEvent>> _frames = new(StringComparer.Ordinal);

    private async Task<IObserver<ManagerEvent>> ServerAsync(string serverId)
    {
        if (_frames.TryGetValue(serverId, out var existing))
        {
            return existing;
        }

        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns("22.10.1");
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => NoEvents());
        IObserver<ManagerEvent>? observer = null;
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => observer = o))
            .Returns(Substitute.For<IDisposable>());

        var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        _servers.Add(server);
        _manager.AttachToServer(server, serverId);
        await server.StartAsync();

        var frames = observer ?? throw new InvalidOperationException("VerbaraServer.StartAsync subscribed no event observer.");
        _frames[serverId] = frames;
        return frames;
    }

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
