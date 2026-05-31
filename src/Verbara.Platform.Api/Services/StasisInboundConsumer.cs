using Verbara.Platform.Api.Health;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Phase 2 voice inbound delivery — the process that consumes ARI Stasis app
/// <c>verbara</c> so inbound calls actually reach a queue (without it the call
/// enters Stasis, nobody acts, and <c>Hangup()</c> drops it).
///
/// <para><b>Leader-gated (ADR-0022 Phase A.5 pattern):</b> the consumer runs on
/// every pod but only the elected leader for
/// <see cref="VoiceLeaderResources.Inbound"/> opens the ARI WebSocket. Asterisk
/// delivers a Stasis app to exactly one WebSocket; two pods on the same app
/// produce <c>ApplicationReplaced</c> churn. Because a physical call cannot be
/// re-emitted, gating the <i>connection</i> (not just per-event fanout) is the
/// correct contract. Leadership is polled every second; connect/disconnect
/// transitions follow the leader flag.</para>
///
/// <para><b>Tenant resolution contract:</b> the inbound <i>trunk</i> channel
/// name is NOT tenant-prefixed (<c>PJSIP/t-{trunkId}-XXXX</c> — trunk isolation
/// lives in <c>ps_endpoints.tenantid</c>, and the trunk store is tenant-scoped
/// so it cannot reverse-resolve a tenant from a trunkId alone). The tenant is
/// therefore read from the <c>TENANT_ID</c> channel variable, which the inbound
/// endpoint sets via <c>set_var</c> (wired in Phase 2.4). Every unresolved step
/// <b>fails closed</b> (Hangup) — never a silent drop back into the dialplan.</para>
///
/// <para><b>StopHost safety:</b> <c>HostOptions.BackgroundServiceExceptionBehavior
/// = StopHost</c> means an unhandled exception here kills the whole host. The
/// consumer logs + returns cleanly when ARI is unconfigured (dev / InMemory),
/// swallows per-tick connect failures, and only rethrows on a truly fatal loop
/// fault (after a Critical <c>WorkerCrash</c> log, so the stop is attributable).</para>
/// </summary>
internal sealed partial class StasisInboundConsumer : BackgroundService
{
    /// <summary>Dialplan context the answered call is continued into (Phase 2.3).</summary>
    public const string StasisQueueContext = "stasis-queue";

    /// <summary>
    /// Fixed dialplan extension in <c>[stasis-queue]</c> (priority-1 <c>Queue(${QUEUE_NAME})</c>).
    /// Continuing to a constant extension + carrying the queue name in a channel variable
    /// decouples the dialplan from the tenant/queue charset: a hyphenated / non-numeric /
    /// mixed-case queue name can never miss an extension pattern and silently drop the call
    /// post-Answer (ARI <c>continue</c> returns 204 regardless of whether the extension matches).
    /// </summary>
    public const string QueueEntryExtension = "s";

    /// <summary>
    /// Channel variable carrying the Asterisk realtime queue name (<c>{tenantId}-{queueName}</c>)
    /// that <c>[stasis-queue]</c> reads as <c>Queue(${QUEUE_NAME})</c>.
    /// </summary>
    public const string QueueNameVariable = "QUEUE_NAME";

    /// <summary>Phase marker emitted by the dialplan as <c>Stasis(verbara,inbound,${EXTEN})</c>.</summary>
    private const string InboundPhase = "inbound";

    /// <summary>Channel variable carrying the owning tenant id (set via ps_endpoints set_var).</summary>
    private const string TenantVariable = "TENANT_ID";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IAriClientFactory _factory;
    private readonly IClusterLeader _leader;
    private readonly IDidRouteStore _didRoutes;
    private readonly IQueueStore _queues;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly ILogger<StasisInboundConsumer> _logger;
    private readonly AriClientOptions? _ariOptions;

    private IAriClient? _client;
    private IDisposable? _subscription;
    private CancellationToken _stoppingToken;

    // Set by the observer (pump thread) when the ARI WebSocket errors/completes; read by
    // EvaluateLeadershipAsync (timer thread) which tears down + reconnects the leader's
    // connection. Without this a dropped socket would leave the leader "connected-but-deaf"
    // (non-null _client, no events) until leadership churned. Volatile cross-thread flag.
    private volatile bool _connectionFaulted;

    public StasisInboundConsumer(
        IAriClientFactory factory,
        [FromKeyedServices(VoiceLeaderResources.Inbound)] IClusterLeader leader,
        IDidRouteStore didRoutes,
        IQueueStore queues,
        IServiceHeartbeat heartbeat,
        IConfiguration configuration,
        ILogger<StasisInboundConsumer> logger)
    {
        _factory = factory;
        _leader = leader;
        _didRoutes = didRoutes;
        _queues = queues;
        _heartbeat = heartbeat;
        _logger = logger;

        var ari = configuration.GetSection("Asterisk:Ari");
        var baseUrl = ari["BaseUrl"];
        _ariOptions = string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : new AriClientOptions
            {
                BaseUrl = baseUrl,
                Username = ari["Username"] ?? "",
                Password = ari["Password"] ?? "",
                Application = ari["Application"] ?? "verbara",
            };
    }

    /// <summary>True when <c>Asterisk:Ari:BaseUrl</c> is configured (voice inbound enabled).</summary>
    internal bool IsAriConfigured => _ariOptions is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            // Stagger past composition-root startup like the other workers.
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            if (_ariOptions is null)
            {
                // Dev / InMemory deployments without an Asterisk: stay idle, never
                // crash the host (StopHost would otherwise take the API down).
                LogAriNotConfigured();
                return;
            }

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _heartbeat.RecordTick(nameof(StasisInboundConsumer), PollInterval);
                try
                {
                    await EvaluateLeadershipAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Transient leadership-eval / connect fault — log + retry next tick.
                    LogLeadershipEvalError(ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(StasisInboundConsumer), fatalEx.Message, fatalEx);
            throw;
        }
        finally
        {
            await TeardownAsync();
        }
    }

    /// <summary>
    /// Reconciles the ARI connection with the current leadership flag: the leader
    /// connects + subscribes; a former leader tears down. Idempotent — repeated
    /// ticks while leadership is unchanged are no-ops.
    /// </summary>
    internal async Task EvaluateLeadershipAsync(CancellationToken ct)
    {
        if (_leader.IsLeader)
        {
            // A faulted socket (observer signalled OnError/OnCompleted) is torn down so the
            // reconnect below re-establishes a live connection — never left connected-but-deaf.
            if (_connectionFaulted && _client is not null)
                await TeardownAsync();

            if (_client is null)
                await ConnectAsync(ct);
        }
        else if (_client is not null)
        {
            await TeardownAsync();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_ariOptions is null)
            return;

        try
        {
            var client = await _factory.CreateAndConnectAsync(_ariOptions, ct);
            // Publish the (already-connected) client to the field BEFORE subscribing, so that
            // if Subscribe throws (e.g. ObjectDisposedException from an early socket drop) the
            // catch's TeardownAsync sees _client and disposes it — no orphaned ARI WebSocket.
            // Events can't arrive until Subscribe attaches the observer, so OnEvent never reads
            // a stale field. _connectionFaulted is cleared for the fresh connection.
            _connectionFaulted = false;
            _client = client;
            _subscription = client.Subscribe(new StasisObserver(this));
            LogConnected(_ariOptions.Application);
        }
        catch (Exception ex)
        {
            LogConnectFailed(ex.Message);
            await TeardownAsync();
        }
    }

    private async Task TeardownAsync()
    {
        _connectionFaulted = false;
        _subscription?.Dispose();
        _subscription = null;

        var client = _client;
        _client = null;
        if (client is null)
            return;

        try { await client.DisconnectAsync(_stoppingToken); }
        catch (Exception ex) { LogTeardownWarning(ex.Message); }

        try { await client.DisposeAsync(); }
        catch (Exception ex) { LogTeardownWarning(ex.Message); }

        LogDisconnected();
    }

    /// <summary>
    /// True only for a <see cref="StasisStartEvent"/> whose first Stasis arg is
    /// the inbound phase marker (<c>Stasis(verbara,inbound,${EXTEN})</c>).
    /// </summary>
    internal static bool IsInboundStart(AriEvent evt) =>
        evt is StasisStartEvent { Args: { Length: > 0 } args }
        && string.Equals(args[0], InboundPhase, StringComparison.OrdinalIgnoreCase);

    /// <summary>Observer callback — dispatched on the leader's ARI WS thread.</summary>
    private void OnEvent(AriEvent value)
    {
        if (!IsInboundStart(value))
            return;

        var client = _client;
        if (client is null)
            return;

        // Fire-and-forget with its own guard (HandleStasisStartAsync never throws),
        // mirroring PushToHubRelay's `_ = SendXxxAsync(...)` dispatch.
        _ = HandleStasisStartAsync(client, (StasisStartEvent)value, _stoppingToken);
    }

    /// <summary>
    /// Answers the inbound call and continues it into the tenant's Asterisk queue
    /// (<c>{tenantId}-{queueName}</c> in the <c>stasis-queue</c> context), or
    /// hangs up if any of tenant / DID / route / queue cannot be resolved.
    /// Never throws — failures are logged and the channel is hung up.
    /// </summary>
    internal async Task HandleStasisStartAsync(IAriClient client, StasisStartEvent evt, CancellationToken ct)
    {
        var channelId = evt.Channel?.Id;
        if (string.IsNullOrEmpty(channelId))
        {
            // No channel id — nothing to act on (can't even hang up). Log so the abandoned
            // (malformed / partial) StasisStart is observable rather than a totally silent return.
            LogChannelIdMissing();
            return;
        }

        try
        {
            var tenant = await ReadTenantAsync(client, channelId, ct);
            if (string.IsNullOrWhiteSpace(tenant))
            {
                LogTenantUnresolved(channelId);
                await SafeHangupAsync(client, channelId, ct);
                return;
            }

            var did = ResolveDid(evt);
            if (string.IsNullOrWhiteSpace(did))
            {
                LogDidMissing(channelId, tenant);
                await SafeHangupAsync(client, channelId, ct);
                return;
            }

            var route = await _didRoutes.GetByDidAsync(new TenantId(tenant), did, ct);
            if (route is null || !route.IsActive)
            {
                LogNoRoute(tenant, did);
                await SafeHangupAsync(client, channelId, ct);
                return;
            }

            var queue = await _queues.GetByIdAsync(new TenantId(tenant), route.QueueId, ct);
            if (queue is null)
            {
                LogQueueMissing(tenant, route.QueueId.Value);
                await SafeHangupAsync(client, channelId, ct);
                return;
            }

            // Asterisk realtime queue name = {tenantId}-{queueName} (RealtimeSyncEngine).
            // Pass it via the QUEUE_NAME channel variable and continue to the fixed extension
            // "s" so the dialplan ([stasis-queue]: exten => s,1,Queue(${QUEUE_NAME})) routes
            // regardless of the queue name's charset — no pattern to miss, no post-Answer drop.
            var asteriskQueue = $"{tenant}-{queue.Name}";
            await client.Channels.AnswerAsync(channelId, ct);
            await client.Channels.SetVariableAsync(channelId, QueueNameVariable, asteriskQueue, ct);
            await client.Channels.ContinueAsync(
                channelId, context: StasisQueueContext, extension: QueueEntryExtension, priority: 1, label: null, ct);
            LogRoutedToQueue(channelId, tenant, did, asteriskQueue);
        }
        catch (Exception ex)
        {
            LogHandleError(channelId, ex.Message);
            await SafeHangupAsync(client, channelId, ct);
        }
    }

    private async Task<string?> ReadTenantAsync(IAriClient client, string channelId, CancellationToken ct)
    {
        try
        {
            var variable = await client.Channels.GetVariableAsync(channelId, TenantVariable, ct);
            return variable?.Value;
        }
        catch (Exception ex)
        {
            // Asterisk returns 404 when the variable is unset — treat as unresolved.
            LogTenantVarReadFailed(channelId, ex.Message);
            return null;
        }
    }

    private static string? ResolveDid(StasisStartEvent evt)
    {
        if (evt.Args is { Length: > 1 } args && !string.IsNullOrWhiteSpace(args[1]))
            return args[1];
        return evt.Channel?.Dialplan?.Exten;
    }

    private async Task SafeHangupAsync(IAriClient client, string channelId, CancellationToken ct)
    {
        try
        {
            await client.Channels.HangupAsync(channelId, ct);
        }
        catch (Exception ex)
        {
            LogHangupFailed(channelId, ex.Message);
        }
    }

    /// <summary>
    /// Marks the current ARI connection as faulted so the next leadership tick tears it down
    /// and reconnects. Called from the observer (pump thread) on OnError/OnCompleted, and
    /// available to tests to drive the reconnect path deterministically.
    /// </summary>
    internal void SignalConnectionFault() => _connectionFaulted = true;

    private sealed class StasisObserver(StasisInboundConsumer owner) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) => owner.OnEvent(value);

        // A WebSocket error or graceful completion means the leader's connection is dead —
        // flag it so EvaluateLeadershipAsync re-establishes it (never connected-but-deaf).
        public void OnError(Exception error) => owner.SignalConnectionFault();
        public void OnCompleted() => owner.SignalConnectionFault();
    }

    // ─── Log messages ───────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Asterisk:Ari not configured — StasisInboundConsumer idle (voice inbound disabled).")]
    private partial void LogAriNotConfigured();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Leader — connected ARI WebSocket for app '{Application}'; consuming inbound Stasis.")]
    private partial void LogConnected(string application);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[STASIS] Failed to connect ARI WebSocket: {Reason}")]
    private partial void LogConnectFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Not leader (or shutting down) — ARI WebSocket disconnected.")]
    private partial void LogDisconnected();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] ARI teardown warning: {Reason}")]
    private partial void LogTeardownWarning(string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Leadership evaluation failed — will retry next tick: {Reason}")]
    private partial void LogLeadershipEvalError(string reason, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] inbound StasisStart with no channel id — abandoning (nothing to act on).")]
    private partial void LogChannelIdMissing();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId}: tenant could not be resolved (TENANT_ID unset) — hanging up.")]
    private partial void LogTenantUnresolved(string channelId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId}: failed to read TENANT_ID variable: {Reason}")]
    private partial void LogTenantVarReadFailed(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}): no DID in Stasis args — hanging up.")]
    private partial void LogDidMissing(string channelId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] tenant {TenantId}: no active did_route for DID '{Did}' — hanging up.")]
    private partial void LogNoRoute(string tenantId, string did);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] tenant {TenantId}: did_route points at missing queue '{QueueId}' — hanging up.")]
    private partial void LogQueueMissing(string tenantId, string queueId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}, DID {Did}) → queue '{Queue}'.")]
    private partial void LogRoutedToQueue(string channelId, string tenantId, string did, string queue);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[STASIS] Channel {ChannelId}: error handling inbound call: {Reason}")]
    private partial void LogHandleError(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId}: hangup failed: {Reason}")]
    private partial void LogHangupFailed(string channelId, string reason);
}
