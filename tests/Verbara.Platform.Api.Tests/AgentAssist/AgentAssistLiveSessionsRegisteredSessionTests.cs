#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup chains .Returns() on ValueTask

using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.AgentAssist.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.AudioSocket;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant, audit F1 (design D2): the production projection
/// <see cref="AgentAssistLiveSessions.Snapshot"/> over a session the real Pro engine registered. Pro's
/// <c>AgentAssistSession</c> constructor and <c>AgentAssistSupervisor.Add</c> are internal, so the session is made the
/// way production makes it: Pro's public <c>AddProAgentAssist</c> runs the engine as a hosted service, a
/// <see cref="CallConnectedEvent"/> starts it, a substituted ARI client answers the snoop requests and two real
/// <see cref="AudioSocketClient"/> connections stand in for Asterisk's snoop audio. No reflection: every value is
/// read through public members.
/// <list type="bullet">
/// <item>Conflict: a session whose start-time snapshot is tenant A, on a call re-stamped to tenant B, is owned by
/// nobody. Fails if the projection drops <c>s.TenantId</c> (the owner becomes B).</item>
/// <item>Late stamp: a session whose snapshot is empty, on a call stamped afterwards, is owned by the call's tenant.
/// Fails if the projection drops <c>s.CallSession</c> (the owner becomes nobody).</item>
/// </list>
/// </summary>
public sealed class AgentAssistLiveSessionsRegisteredSessionTests
{
    private const string Acme = "acme";
    private const string Globex = "globex";
    private const string ServerId = "server-1";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Snapshot_ShouldOwnNothing_WhenARegisteredSessionsSnapshotTenantDiffersFromItsRestampedCall()
    {
        var call = new CallSession("aa-reg-conflict", "1712345678.20", ServerId, CallDirection.Outbound) { TenantId = Acme };
        await using var engine = await RegisteredEngine.StartAsync(call);

        call.TenantId = Globex;   // re-stamped after the session took its snapshot

        var session = engine.Supervisor.ActiveSessions[call.SessionId];
        session.TenantId.Should().Be(Acme, "precondition: the engine recorded the call's tenant at start");
        session.CallSession!.TenantId.Should().Be(Globex, "precondition: the call now carries another tenant");

        var live = new AgentAssistLiveSessions(engine.Supervisor).Snapshot();

        live.Should().ContainSingle().Which.Should().Be(new LiveAssistSession(call.SessionId, null, call),
            "a session whose call and recorded tenant disagree is owned by no tenant");
    }

    [Fact]
    public async Task Snapshot_ShouldOwnTheCallTenant_WhenARegisteredSessionsSnapshotIsEmptyAndTheCallIsStampedLater()
    {
        var call = new CallSession("aa-reg-late", "1712345678.21", ServerId, CallDirection.Inbound);
        await using var engine = await RegisteredEngine.StartAsync(call);

        call.TenantId = Acme;   // stamped after the session took its (empty) snapshot

        engine.Supervisor.ActiveSessions[call.SessionId].TenantId.Should().BeEmpty("precondition: the snapshot is empty");

        var live = new AgentAssistLiveSessions(engine.Supervisor).Snapshot();

        live.Should().ContainSingle().Which.Should().Be(new LiveAssistSession(call.SessionId, Acme, call),
            "the call's late stamp is read through the registered session's call");
    }

    /// <summary>The Pro engine running in a bare container, with one session registered for a given call.</summary>
    private sealed class RegisteredEngine : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hosted;
        private readonly IReadOnlyList<AudioSocketClient> _clients;
        private readonly SubscribedSignal _events;

        private RegisteredEngine(
            ServiceProvider provider, IReadOnlyList<IHostedService> hosted, IReadOnlyList<AudioSocketClient> clients,
            SubscribedSignal events)
        {
            _provider = provider;
            _events = events;
            _hosted = hosted;
            _clients = clients;
            Supervisor = provider.GetRequiredService<AgentAssistSupervisor>();
        }

        public AgentAssistSupervisor Supervisor { get; }

        public static async Task<RegisteredEngine> StartAsync(CallSession call)
        {
            using var guard = new CancellationTokenSource(Bound);
            var events = new SubscribedSignal();
            var sessions = Substitute.For<ICallSessionManager>();
            sessions.Events.Returns(events);
            sessions.GetById(call.SessionId).Returns(call);

            var audio = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton(sessions);
            services.AddSingleton(PoolWithAri());
            services.AddSingleton(audio);                                  // the engine's TryAdd keeps this one
            services.AddSingleton<ILicenseGuard>(new AllowEverything());   // AddProLicenseGuard's TryAdd keeps this one
            services.AddTransient<SpeechRecognizer, SilentRecognizer>();
            services.AddProAgentAssist();

            var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
                await service.StartAsync(guard.Token);

            // The engine subscribes after its AudioSocket server is listening.
            await events.Subscribed.WaitAsync(guard.Token);

            // Asterisk's two snoop connections; the engine queues them until it asks for them.
            var clients = new List<AudioSocketClient>();
            for (var i = 0; i < 2; i++)
            {
                var client = new AudioSocketClient("127.0.0.1", audio.BoundPort, Guid.NewGuid());
                await client.ConnectAsync(guard.Token);
                clients.Add(client);
            }

            var engine = new RegisteredEngine(provider, hosted, clients, events);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (engine.Supervisor.SessionStarted.Subscribe(s =>
                   {
                       if (s.SessionId == call.SessionId)
                           registered.TrySetResult();
                   }))
            {
                events.OnNext(new CallConnectedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, "agent-1", "support", TimeSpan.Zero));
                await registered.Task.WaitAsync(guard.Token);
            }

            return engine;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in _hosted)
                await service.StopAsync(CancellationToken.None);
            foreach (var client in _clients)
                await client.DisposeAsync();
            await _provider.DisposeAsync();
            _events.Dispose();
        }

        private static VerbaraServerPool PoolWithAri()
        {
            var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
            var ami = Substitute.For<IAmiConnection>();
            ami.State.Returns(AmiConnectionState.Connected);
            var server = new VerbaraServer(ami, NullLogger<VerbaraServer>.Instance);

            var channels = Substitute.For<IAriChannelsResource>();
            channels.SnoopAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(c => ValueTask.FromResult(new AriChannel { Id = c.ArgAt<string?>(4) ?? "snoop" }));
            channels.ContinueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask);
            channels.HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
            var ari = Substitute.For<IAriClient>();
            ari.Channels.Returns(channels);

            server.SetAriClient(ari);
            pool.AddExistingServer(ServerId, server);
            return pool;
        }
    }

    /// <summary>The session-event stream, with a signal for the moment the engine subscribes to it.</summary>
    private sealed class SubscribedSignal : IObservable<SessionDomainEvent>, IDisposable
    {
        private readonly Subject<SessionDomainEvent> _events = new();
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Subscribed => _subscribed.Task;

        public IDisposable Subscribe(IObserver<SessionDomainEvent> observer)
        {
            var subscription = _events.Subscribe(observer);
            _subscribed.TrySetResult();
            return subscription;
        }

        public void OnNext(SessionDomainEvent value) => _events.OnNext(value);

        public void Dispose() => _events.Dispose();
    }

    private sealed class AllowEverything : ILicenseGuard
    {
        public ValueTask<LicenseGuardResult> CanExecuteAsync(LicenseFeature feature, CancellationToken ct) =>
            ValueTask.FromResult(CanExecute(feature));

        public LicenseGuardResult CanExecute(LicenseFeature feature) => new(Allowed: true, Reason: null);
    }

    /// <summary>A recognizer that drains the audio and recognizes nothing: a quiet call.</summary>
    private sealed class SilentRecognizer : SpeechRecognizer
    {
        public override string ProviderName => "Silent";

        public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
            AudioFormat format,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            {
                // Each frame is read and dropped: nothing in it is recognized.
            }

            yield break;
        }
    }
}
