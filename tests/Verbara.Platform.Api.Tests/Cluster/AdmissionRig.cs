#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup returns ValueTask

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Disposables;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Verbara.Platform.Conversations;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.Cluster;

/// <summary>One live call in a fake Asterisk's channel table: its channel, dialplan position and channel variables.</summary>
internal sealed record LiveCall(string Label, string UniqueId, string Channel, string Context, string Exten,
    IReadOnlyDictionary<string, string> Variables)
{
    public string LinkedId => UniqueId;
}

/// <summary>
/// A fake Asterisk node: an <see cref="IAmiConnection"/> substitute whose <c>Status</c> lists the node's live calls and
/// whose <c>GetVar</c> answers each channel's variables, counted at the connection by variable. Over a connection
/// that is not the Sdk's <c>AmiConnection</c> the load's barrier counts as reached, so no <c>Status</c> is withheld.
/// </summary>
internal sealed class FakeAsteriskNode
{
    private readonly ConcurrentDictionary<string, LiveCall> _byChannel = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _getVars = new();
    private int _statusReads;

    public FakeAsteriskNode(string hostname) => Hostname = hostname;

    /// <summary>A new connection to this node, as each connect of the cluster manager opens one.</summary>
    public IAmiConnection Connect()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.State.Returns(_ => Volatile.Read(ref _dead) == 1 && ReferenceEquals(connection, Connection)
            ? AmiConnectionState.Disconnected : AmiConnectionState.Connected);
        connection.AsteriskVersion.Returns("22.10.1");
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<ManagerAction>(0) is StatusAction ? StatusRows() : NoEvents());
        connection.SendActionAsync<GetVarResponse>(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<GetVarResponse>(GetVar((GetVarAction)ci.ArgAt<ManagerAction>(0))));
        connection.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ManagerResponse>(new ManagerResponse { Response = "Success" }));
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => Frames = o)).Returns(Substitute.For<IDisposable>());
        Volatile.Write(ref _dead, 0);
        Connection = connection;
        Interlocked.Increment(ref _connects);
        return connection;
    }

    private int _dead;
    private int _connects;

    /// <summary>How many connections the cluster manager opened to this node.</summary>
    public int Connects => Volatile.Read(ref _connects);

    /// <summary>The current connection reads <see cref="AmiConnectionState.Disconnected"/> from now on.</summary>
    public void KillCurrentConnection() => Volatile.Write(ref _dead, 1);

    public string Hostname { get; }

    public IAmiConnection? Connection { get; private set; }

    /// <summary>The observer the server subscribed to the connection's events, once it started.</summary>
    public IObserver<ManagerEvent>? Frames { get; private set; }

    public int StatusReads => Volatile.Read(ref _statusReads);

    public void Hold(LiveCall call) => _byChannel[call.Channel] = call;

    /// <summary>GetVar actions the connection answered, as <c>label:VARIABLE</c>, in order.</summary>
    public IReadOnlyList<string> GetVars => [.. _getVars];

    public void ClearGetVars() => _getVars.Clear();

    /// <summary>A live start of <paramref name="call"/> through the connection's event stream.</summary>
    public void Start(LiveCall call)
    {
        Hold(call);
        Frames!.OnNext(new NewChannelEvent
        {
            EventType = "Newchannel", UniqueId = call.UniqueId, Channel = call.Channel, ChannelState = "Up",
            CallerIdNum = "5551234", Context = call.Context, Exten = call.Exten, Priority = 1,
            Linkedid = call.LinkedId,
            RawFields = Raw(call),
        });
    }

    /// <summary>Ends <paramref name="call"/> through the connection's event stream.</summary>
    public void End(LiveCall call)
    {
        _byChannel.TryRemove(call.Channel, out _);
        Frames!.OnNext(new HangupEvent
        {
            EventType = "Hangup", UniqueId = call.UniqueId, Channel = call.Channel, LinkedId = call.LinkedId, Cause = 16,
            CauseTxt = "Normal Clearing",
            RawFields = new Dictionary<string, string>(Raw(call)) { ["Cause"] = "16", ["Cause-txt"] = "Normal Clearing" },
        });
    }

    private GetVarResponse GetVar(GetVarAction action)
    {
        var channel = action.Channel ?? "";
        _byChannel.TryGetValue(channel, out var call);
        _getVars.Enqueue($"{call?.Label ?? channel}:{action.Variable}");
        if (call is null)
            return new GetVarResponse { Response = "Error", Message = "No such channel" };
        return call.Variables.TryGetValue(action.Variable ?? "", out var value)
            ? new GetVarResponse { Response = "Success", Variable = action.Variable, Value = value }
            : new GetVarResponse { Response = "Success", Variable = action.Variable, Value = "" };
    }

    private async IAsyncEnumerable<ManagerEvent> StatusRows()
    {
        Interlocked.Increment(ref _statusReads);
        await Task.CompletedTask;
        foreach (var call in _byChannel.Values.OrderBy(c => c.UniqueId, StringComparer.Ordinal))
        {
            yield return new StatusEvent
            {
                EventType = "Status", UniqueId = call.UniqueId, Channel = call.Channel, LinkedId = call.LinkedId,
                RawFields = Raw(call),
            };
        }
    }

    private static Dictionary<string, string> Raw(LiveCall call) => new(StringComparer.Ordinal)
    {
        ["Channel"] = call.Channel, ["Uniqueid"] = call.UniqueId, ["Linkedid"] = call.LinkedId, ["ChannelState"] = "6",
        ["ChannelStateDesc"] = "Up", ["CallerIDNum"] = "5551234", ["Context"] = call.Context, ["Exten"] = call.Exten,
        ["Priority"] = "1",
    };

    private static async IAsyncEnumerable<ManagerEvent> NoEvents()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>The AMI connection factory the cluster manager connects nodes through: each hostname is one fake node.</summary>
internal sealed class FakeAmiConnectionFactory : IAmiConnectionFactory
{
    private readonly ConcurrentDictionary<string, FakeAsteriskNode> _nodes = new(StringComparer.Ordinal);

    public FakeAsteriskNode Node(string hostname) => _nodes.GetOrAdd(hostname, h => new FakeAsteriskNode(h));

    public IAmiConnection Create(AmiConnectionOptions options) => Node(options.Hostname).Connect();

    public ValueTask<IAmiConnection> CreateAndConnectAsync(AmiConnectionOptions options, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Node(options.Hostname).Connect());
}

/// <summary>When the admission was entered and when it returned, relative to a <c>Subscribe</c>.</summary>
internal enum AdmissionPhase
{
    Before,
    During,
    After,
}

/// <summary>How the decorator's <c>AdmitExistingCalls</c> runs.</summary>
internal enum AdmissionMode
{
    /// <summary>Forwards to the real manager and returns its <see cref="ValueTask"/> unchanged.</summary>
    Forward,

    /// <summary>Boot control (ii)'s failing variant: a discarded admission that runs only once the test opens its gate.</summary>
    Discard,

    /// <summary>The mutation: the Sdk interface's default body, which admits nothing.</summary>
    Disabled,
}

/// <summary>
/// The test decorator over the real Sdk <see cref="CallSessionManager"/>: forwards every member, and its
/// <see cref="Events"/> records each <c>Subscribe</c> (subscriber identity, ordinal, admission phase), the observable
/// subscription signal the arms wait on.
/// </summary>
internal sealed class RecordingSessionManager : ICallSessionManager
{
    private readonly CallSessionManager _inner;
    private readonly RecordingEvents _events;
    private int _phase;

    public RecordingSessionManager(CallSessionManager inner)
    {
        _inner = inner;
        _events = new RecordingEvents(inner.Events, this);
    }

    public AdmissionMode Mode { get; set; } = AdmissionMode.Forward;

    /// <summary>The gate a <see cref="AdmissionMode.Discard"/> admission waits on.</summary>
    public TaskCompletionSource DiscardGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A subscriber identity whose forwarding to the inner stream waits until the admission returned (boot-b).</summary>
    public string? DeferUntilAdmitted { get; set; }

    public AdmissionPhase Phase => (AdmissionPhase)Volatile.Read(ref _phase);

    public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> AdmissionLog { get; } = new();

    public RecordingEvents Recorder => _events;

    public CallSession? GetById(string sessionId) => _inner.GetById(sessionId);
    public CallSession? GetByLinkedId(string linkedId) => _inner.GetByLinkedId(linkedId);
    public CallSession? GetByChannelId(string uniqueId) => _inner.GetByChannelId(uniqueId);
    public CallSession? GetByBridgeId(string bridgeId) => _inner.GetByBridgeId(bridgeId);
    public CallSession? GetByLinkedId(string serverId, string linkedId) => _inner.GetByLinkedId(serverId, linkedId);
    public CallSession? GetByChannelId(string serverId, string uniqueId) => _inner.GetByChannelId(serverId, uniqueId);
    public CallSession? GetByBridgeId(string serverId, string bridgeId) => _inner.GetByBridgeId(serverId, bridgeId);
    public IEnumerable<CallSession> ActiveSessions => _inner.ActiveSessions;
    public IEnumerable<CallSession> GetRecentCompleted(int count = 100) => _inner.GetRecentCompleted(count);
    public IObservable<SessionDomainEvent> Events => _events;
    public void AttachToServer(VerbaraServer server, string serverId) => _inner.AttachToServer(server, serverId);
    public void DetachFromServer(string serverId) => _inner.DetachFromServer(serverId);
    public IReadOnlyList<CallSession> DetachFromServer(string serverId, DetachedCalls calls) => _inner.DetachFromServer(serverId, calls);
    public bool RegisterReconstructedSession(CallSession session) => _inner.RegisterReconstructedSession(session);
    // The container owns the real manager (registered as its own singleton) and disposes it once.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask AdmitExistingCalls(string serverId, CancellationToken cancellationToken = default)
    {
        AdmissionLog.Enqueue($"admission entered: {serverId} ({Mode})");
        Volatile.Write(ref _phase, (int)AdmissionPhase.During);
        switch (Mode)
        {
            case AdmissionMode.Discard:
                _ = Task.Run(async () =>
                {
                    await DiscardGate.Task.ConfigureAwait(false);
                    await _inner.AdmitExistingCalls(serverId, cancellationToken).ConfigureAwait(false);
                    Returned(serverId);
                }, CancellationToken.None);
                return ValueTask.CompletedTask;
            case AdmissionMode.Disabled:
                Returned(serverId);
                return ValueTask.CompletedTask;
            default:
                return ForwardAsync(serverId, cancellationToken);
        }
    }

    private async ValueTask ForwardAsync(string serverId, CancellationToken cancellationToken)
    {
        try
        {
            await _inner.AdmitExistingCalls(serverId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Returned(serverId);
        }
    }

    private void Returned(string serverId)
    {
        Volatile.Write(ref _phase, (int)AdmissionPhase.After);
        AdmissionLog.Enqueue($"admission returned: {serverId}");
        Admitted.TrySetResult();
    }
}

/// <summary>The recording wrapper of the session-event stream.</summary>
internal sealed class RecordingEvents(IObservable<SessionDomainEvent> inner, RecordingSessionManager owner)
    : IObservable<SessionDomainEvent>
{
    private readonly ConcurrentQueue<Subscription> _log = new();
    private int _ordinal;

    internal sealed record Subscription(int Ordinal, string Subscriber, AdmissionPhase Phase, bool Deferred);

    public IReadOnlyList<Subscription> Log => [.. _log];

    /// <summary>The delivered events per subscriber, as <c>EventType:linkedId</c>.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<string>> Deliveries { get; } = new(StringComparer.Ordinal);

    public bool HasSubscribed(string subscriber) => _log.Any(s => s.Subscriber == subscriber);

    /// <summary>Raised before an event is handed to a subscriber, with the subscriber's identity.</summary>
    public event Action<string, SessionDomainEvent>? Delivering;

    internal void OnDelivering(string subscriber, SessionDomainEvent value) => Delivering?.Invoke(subscriber, value);

    /// <summary>The phase of <paramref name="subscriber"/>'s first subscription, or <see langword="null"/>.</summary>
    public Subscription? Of(string subscriber) => _log.FirstOrDefault(s => s.Subscriber == subscriber);

    /// <summary>The events delivered to <paramref name="subscriber"/>, as <c>EventType:linkedId</c>.</summary>
    public IReadOnlyList<string> DeliveredTo(string subscriber) =>
        Deliveries.TryGetValue(subscriber, out var q) ? [.. q] : [];

    public IDisposable Subscribe(IObserver<SessionDomainEvent> observer)
    {
        var subscriber = Identify(observer);
        var deferred = owner.DeferUntilAdmitted == subscriber && owner.Phase != AdmissionPhase.After;
        _log.Enqueue(new Subscription(Interlocked.Increment(ref _ordinal), subscriber, owner.Phase, deferred));
        var deliveries = Deliveries.GetOrAdd(subscriber, _ => new ConcurrentQueue<string>());
        var counting = new CountingObserver(observer, deliveries, owner, this, subscriber);
        if (!deferred)
            return inner.Subscribe(counting);

        var forwarded = new SingleAssignmentDisposable();
        _ = owner.Admitted.Task.ContinueWith(_ => forwarded.Disposable = inner.Subscribe(counting),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return forwarded;
    }

    /// <summary>
    /// The subscriber: the first frame of the subscribing stack whose type is outside the stream's plumbing (the Sdk
    /// sessions, Rx, the runtime and this rig), unwrapped from compiler-generated state machines and closures.
    /// </summary>
    private static string Identify(IObserver<SessionDomainEvent> observer)
    {
        foreach (var type in new StackTrace(1, false).GetFrames().Select(f => Outermost(f.GetMethod()?.DeclaringType)))
        {
            // The Sdk's agent and queue trackers subscribe from their constructors, inside the Sdk's own namespace.
            if (type?.Name.EndsWith("SessionTracker", StringComparison.Ordinal) == true)
                return type.Name;
            var ns = type?.Namespace ?? "";
            if (type is null || type == typeof(RecordingEvents) || ns.StartsWith("System", StringComparison.Ordinal)
                || ns.StartsWith("Verbara.Sdk.Sessions.Manager", StringComparison.Ordinal)
                || ns.StartsWith("Verbara.Sdk.Sessions.Internal", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft", StringComparison.Ordinal))
                continue;
            return type.Name;
        }

        return observer.GetType().Name;

        static Type? Outermost(Type? type)
        {
            while (type is not null && type.Name.StartsWith('<') && type.DeclaringType is not null)
                type = type.DeclaringType;
            return type;
        }
    }

    private sealed class CountingObserver(IObserver<SessionDomainEvent> inner, ConcurrentQueue<string> deliveries,
        RecordingSessionManager owner, RecordingEvents recorder, string subscriber) : IObserver<SessionDomainEvent>
    {
        public void OnCompleted() => inner.OnCompleted();
        public void OnError(Exception error) => inner.OnError(error);

        public void OnNext(SessionDomainEvent value)
        {
            var linked = owner.GetById(value.SessionId)?.LinkedId ?? value.SessionId;
            deliveries.Enqueue($"{value.GetType().Name}:{linked}");
            recorder.OnDelivering(subscriber, value);
            inner.OnNext(value);
        }
    }
}

/// <summary>Records the messages of the event store's subscriber, whose writer edge the arms wait on.</summary>
internal sealed class RecordingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public bool Contains(string message) => _messages.Contains(message);

    public ILogger CreateLogger(string categoryName) =>
        categoryName.EndsWith("EventStoreSubscriber", StringComparison.Ordinal) ? new Recorder(_messages) : NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}

/// <summary>
/// The real Api <c>Program</c> over one Testcontainers Postgres for the Dialer, Analytics and Cluster connection
/// strings and an Api Asterisk node (<c>Asterisk:Ami</c>), with: every hosted service <c>Program.cs</c> registered kept
/// in its registration order under <see cref="HostedKey"/> (not started by the host: the arm starts them), the
/// session manager replaced by <see cref="RecordingSessionManager"/>, the AMI connection factory by
/// <see cref="FakeAmiConnectionFactory"/>, and the licensing stubs.
/// </summary>
internal sealed class AdmissionHostFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string HostedKey = "admission-under-test";
    public const string PrimaryHost = "primary.admission.invalid";

    public FakeAmiConnectionFactory Asterisk { get; } = new();

    public RecordingLogs Logs { get; } = new();

    /// <summary>The implementation names of the hosted services <c>Program.cs</c> registered, in order.</summary>
    public IReadOnlyList<string> HostedOrder { get; private set; } = [];

    /// <summary>Whether any registration other than the Sdk's resolves the concrete <see cref="CallSessionManager"/>.</summary>
    public int ConcreteManagerRegistrations { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Dialer", connectionString);
        builder.UseSetting("ConnectionStrings:Analytics", connectionString);
        builder.UseSetting("ConnectionStrings:Cluster", connectionString);
        builder.UseSetting("Asterisk:Ami:Hostname", PrimaryHost);
        builder.UseSetting("Asterisk:Ami:Port", "5038");
        builder.UseSetting("Asterisk:Ami:Username", "admission");
        builder.UseSetting("Asterisk:Ami:Password", "admission");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
            HostedOrder = [.. hosted.Select(Name)];
            foreach (var d in hosted)
                services.Remove(d);
            foreach (var d in hosted)
                services.Add(Keyed(d));

            ConcreteManagerRegistrations = services.Count(d => d.ServiceType == typeof(CallSessionManager));
            foreach (var d in services.Where(d => d.ServiceType == typeof(ICallSessionManager)).ToList())
                services.Remove(d);
            services.AddSingleton<CallSessionManager>();
            services.AddSingleton<RecordingSessionManager>(sp => new RecordingSessionManager(sp.GetRequiredService<CallSessionManager>()));
            services.AddSingleton<ICallSessionManager>(sp => sp.GetRequiredService<RecordingSessionManager>());

            foreach (var d in services.Where(d => d.ServiceType == typeof(IAmiConnectionFactory)).ToList())
                services.Remove(d);
            services.AddSingleton<IAmiConnectionFactory>(Asterisk);

            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton(Logs);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });
        return base.CreateHost(builder);
    }

    private static string Name(ServiceDescriptor d) =>
        d.ImplementationType?.Name ?? d.ImplementationInstance?.GetType().Name ?? "factory";

    private static ServiceDescriptor Keyed(ServiceDescriptor d)
    {
        if (d.ImplementationType is { } type)
            return new ServiceDescriptor(typeof(IHostedService), HostedKey, type, d.Lifetime);
        if (d.ImplementationInstance is { } instance)
            return new ServiceDescriptor(typeof(IHostedService), HostedKey, instance);
        var factory = d.ImplementationFactory!;
        return new ServiceDescriptor(typeof(IHostedService), HostedKey, (sp, _) => factory(sp), d.Lifetime);
    }
}

/// <summary>A Testcontainers Postgres for one rig.</summary>
internal sealed class AdmissionPostgres
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;Application Name=admission-8-8";

    public async Task StartAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _container.StartAsync();
    }

    public async Task<IReadOnlyList<string>> QueryAsync(string sql)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(string.Join(" | ", cells));
        }

        return rows;
    }

    public async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

/// <summary>The live calls of every arm, one per leg kind.</summary>
internal static class AdmissionCalls
{
    public const string Tenant = "acme";
    public const string OtherTenant = "globex";

    public static IReadOnlyList<LiveCall> Table(string prefix, string linkedConversationId)
    {
        var n = 0;
        string Id() => $"{prefix}.{++n}";
        return
        [
            new LiveCall("inbound-trunk", Id(), $"PJSIP/trunk-{prefix}-1", "from-trunk", "500",
                new Dictionary<string, string> { ["TENANT_ID"] = Tenant }),
            new LiveCall("outbound-linked", Id(), $"PJSIP/acme-agent-u1-{prefix}-2", "outbound-agent", "+15551234567",
                new Dictionary<string, string> { ["VERBARA_OUTBOUND_ID"] = linkedConversationId, ["TENANT_ID"] = Tenant }),
            new LiveCall("campaign", Id(), $"PJSIP/trunk-{prefix}-3", "dialer-answered", "s",
                new Dictionary<string, string>
                {
                    ["VERBARA_OUTBOUND_ID"] = "", ["DIALER_CAMPAIGN_ID"] = "42", ["DIALER_CONTACT_ID"] = "7",
                    ["DIALER_ACTION_ID"] = $"act-{prefix}-3", ["TENANT_ID"] = Tenant,
                }),
            new LiveCall("trunk-not-campaign", Id(), $"PJSIP/trunk-{prefix}-4", "from-trunk", "501",
                new Dictionary<string, string> { ["TENANT_ID"] = OtherTenant }),
            new LiveCall("not-inbound", Id(), $"PJSIP/acme-u5-{prefix}-5", "from-internal", "1002",
                new Dictionary<string, string> { ["TENANT_ID"] = Tenant }),
        ];
    }
}

/// <summary>Writes the normalized observations of one arm: stable text, no session ids or timestamps.</summary>
internal static class AdmissionReport
{
    public static string Sessions(ICallSessionManager sessions, IReadOnlyList<LiveCall> calls)
    {
        var sb = new StringBuilder();
        foreach (var call in calls)
        {
            var s = sessions.ActiveSessions.FirstOrDefault(x => x.LinkedId == call.LinkedId);
            if (s is null)
            {
                sb.Append(call.Label).AppendLine(": no session");
                continue;
            }

            var meta = string.Join(",", s.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
            sb.Append(CultureInfo.InvariantCulture,
                $"{call.Label}: dir={s.Direction} tenant={(string.IsNullOrEmpty(s.TenantId) ? "-" : s.TenantId)} meta=[{meta}]").AppendLine();
        }

        return sb.ToString();
    }

    public static async Task<string> ConversationsAsync(IConversationStore store, IReadOnlyList<LiveCall> calls)
    {
        var sb = new StringBuilder();
        foreach (var call in calls)
        {
            var c = await store.FindByVoiceLinkedIdAcrossTenantsAsync(call.LinkedId, CancellationToken.None);
            var line = c is null
                ? $"{call.Label}: no conversation"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{call.Label}: conversation tenant={c.TenantId.Value} state={c.State} owner={c.Owner?.Kind.ToString() ?? "-"} meta=[{string.Join(",", c.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))}]");
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public static string Subscriptions(RecordingEvents recorder) =>
        string.Join(Environment.NewLine, recorder.Log.Select(s =>
            $"#{s.Ordinal} {s.Subscriber} {s.Phase}{(s.Deferred ? " (deferred)" : "")}"));

    public static string Deliveries(RecordingEvents recorder, IReadOnlyList<LiveCall> calls)
    {
        string Label(string linked) => calls.FirstOrDefault(c => c.LinkedId == linked)?.Label ?? "other";
        return string.Join(Environment.NewLine, recorder.Deliveries.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            $"{kv.Key}: {string.Join(",", kv.Value.Select(d => d.Split(':') is [var type, var linked] ? $"{type}({Label(linked)})" : d))}"));
    }

    /// <summary>
    /// Every push on the Api's bus, by its <see cref="PushEvent.EventType"/> string, with its <c>NewState</c> when it
    /// has one. Read by name, not by type, so the rig names no Pro event type (the Pro conversation bridge's
    /// <c>conversation.state.changed</c> included) and keeps compiling when Pro deletes one. Test-only reflection.
    /// </summary>
    public static string Pushes(IEnumerable<PushEvent> pushes) =>
        string.Join(Environment.NewLine, pushes.Select(p =>
            $"push {p.EventType}{(p.GetType().GetProperty("NewState")?.GetValue(p) is string state ? " " + state : "")} tenant={p.Metadata?.TenantId ?? "-"}"));
}
