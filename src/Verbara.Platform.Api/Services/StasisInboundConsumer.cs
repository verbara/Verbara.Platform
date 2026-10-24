using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Typification.Resolution;
using Verbara.Sdk;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Phase 2 voice inbound delivery — the process that consumes ARI Stasis app
/// <c>verbara</c> so inbound calls actually reach a queue (without it the call
/// enters Stasis, nobody acts, and <c>Hangup()</c> drops it).
///
/// <para><b>One registrant (Platform/ADR-0044):</b> Asterisk hands a Stasis app to exactly one
/// ARI WebSocket, the last one to register it. The consumer runs on every pod but only the
/// elected leader for <see cref="VoiceLeaderResources.Inbound"/> opens a WebSocket for the
/// dialplan's app (<see cref="StasisAppNames.Consumer"/>); the per-node ARI client of every
/// replica registers its own app (<see cref="StasisAppNames.Node"/>), and the Api refuses to
/// start when the two names are equal. Leadership is polled every second; connect/disconnect
/// transitions follow the leader flag.</para>
///
/// <para><b>Replaced registration:</b> if another client still takes the app, the live
/// connection receives <c>ApplicationReplaced</c> (its socket stays open, so no fault is ever
/// reported). The consumer logs a Warning, counts it on <c>voice_stasis_app_replaced_total</c>
/// and re-registers under a 1/2/4/8/16-s backoff, at most 5 times per rolling 10 minutes (an
/// Error names the cap), never giving up while leader. A client whose own Sdk reconnects ended
/// (<see cref="AriConnectionState.Faulted"/>) is replaced too.</para>
///
/// <para><b>Make-before-break:</b> a replacement connection is created, subscribed and only then
/// connected (so an event delivered while connecting is never lost), and published before the
/// old one is retired. Retiring closes the old event WebSocket at once (releasing the app on
/// leadership loss, and ending the Sdk's own reconnects) but keeps its REST client until the
/// call handlers started from its events finish or 30 s pass. Every handler uses the client of
/// the connection its <c>StasisStart</c> arrived on.</para>
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

    /// <summary>
    /// Phase marker emitted by <c>[dialer-answered]</c> as <c>Stasis(verbara,campaign,${DIALER_CAMPAIGN_ID})</c>
    /// (dialer-places-calls-end-to-end, design D11).
    /// </summary>
    private const string CampaignPhase = "campaign";

    /// <summary>Channel variable carrying the owning tenant id (set via ps_endpoints set_var).</summary>
    private const string TenantVariable = "TENANT_ID";

    /// <summary>
    /// Channel variable carrying the resolved reason taxonomy path (JSON array of node Codes,
    /// e.g. <c>["CITAS","REPROG"]</c>) for the DID/queue the inbound call arrived on (Typification
    /// P1 implicit voice capture). <see cref="VoiceConversationBridge"/> reads it back via AMI
    /// <c>GetVar</c> when the Conversation is created and stamps <c>Conversation.Metadata["reasonPath"]</c>.
    /// </summary>
    private const string ReasonVariable = "VERBARA_REASON";

    /// <summary>Meter carrying <c>voice.stasis.app_replaced</c> (exported as <c>voice_stasis_app_replaced_total</c>).</summary>
    public const string MeterName = "Verbara.Platform.Voice.Stasis";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a retired connection keeps its REST client for the handlers still running on it.</summary>
    internal static readonly TimeSpan DrainDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Rolling window of the replacement-driven reconnect cap.</summary>
    internal static readonly TimeSpan ReplacementWindow = TimeSpan.FromMinutes(10);

    /// <summary>Replacement-driven reconnects allowed per <see cref="ReplacementWindow"/>; delays 1, 2, 4, 8, 16 s.</summary>
    internal const int MaxReplacementReconnects = 5;

    private static readonly TimeSpan FirstReplacementDelay = TimeSpan.FromSeconds(1);

    private readonly IAriClientFactory _factory;
    private readonly IClusterLeader _leader;
    private readonly IDidRouteStore _didRoutes;
    private readonly IQueueStore _queues;
    private readonly IReasonHintResolver _reasonHints;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly ILogger<StasisInboundConsumer> _logger;
    private readonly AriClientOptions? _ariOptions;

    private readonly TimeProvider _time;
    private readonly Meter _meter;
    private readonly Counter<long> _appReplaced;

    // The dialer's campaign store, null when the dialer is not configured (dialer-places-calls-end-to-end, design
    // D11): the campaign phase loads the answered leg's campaign through it, tenant-scoped.
    private readonly CampaignStoreBase? _campaigns;

    // Guards the connection states, _current and the replacement schedule: the observer (Sdk pump
    // thread) and EvaluateLeadershipAsync (timer thread) both move them.
    private readonly Lock _gate = new();
    private Connection? _current;
    private DateTimeOffset? _reconnectDue;
    private readonly Queue<DateTimeOffset> _replacementReconnects = new();
    private DateTimeOffset? _lastCapError;

    // Retirements in progress (drain then dispose), awaited on shutdown.
    private readonly ConcurrentDictionary<Task, byte> _retirements = new();
    private CancellationToken _stoppingToken;

    public StasisInboundConsumer(
        IAriClientFactory factory,
        [FromKeyedServices(VoiceLeaderResources.Inbound)] IClusterLeader leader,
        IDidRouteStore didRoutes,
        IQueueStore queues,
        IReasonHintResolver reasonHints,
        IServiceHeartbeat heartbeat,
        IConfiguration configuration,
        ILogger<StasisInboundConsumer> logger,
        TimeProvider? timeProvider = null,
        IMeterFactory? meterFactory = null,
        CampaignStoreBase? campaigns = null)
    {
        _factory = factory;
        _campaigns = campaigns;
        _leader = leader;
        _didRoutes = didRoutes;
        _queues = queues;
        _reasonHints = reasonHints;
        _heartbeat = heartbeat;
        _logger = logger;

        _time = timeProvider ?? TimeProvider.System;

        // The dialplan's app, from the same resolution as the node client's own app (ADR-0044).
        _ariOptions = StasisAppNames.ConsumerOptions(configuration);

        _meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _appReplaced = _meter.CreateCounter<long>(
            "voice.stasis.app_replaced", unit: "{replacement}",
            description: "ApplicationReplaced events the inbound-voice leader's live ARI connection received for the dialplan's Stasis app.");
    }

    /// <summary>True when <c>Asterisk:Ari:BaseUrl</c> is configured (voice inbound enabled).</summary>
    internal bool IsAriConfigured => _ariOptions is not null;

    /// <summary>Completes when every retirement started so far has drained and disposed its client.</summary>
    internal Task PendingRetirementsAsync() => Task.WhenAll(_retirements.Keys);

    /// <inheritdoc />
    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }

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
            // Shutdown: close the leader's socket at once, then let in-flight handlers finish
            // (each retirement is bounded by DrainDeadline; the handlers' token is already cancelled).
            if (TakeCurrent() is { } last)
            {
                Retire(last);
                LogDisconnected();
            }

            await PendingRetirementsAsync();
        }
    }

    /// <summary>
    /// Reconciles the ARI connection with the current leadership flag: the leader connects; a
    /// former leader retires its connection; a live connection whose app was replaced (after its
    /// backoff delay) or whose client is <see cref="AriConnectionState.Faulted"/> is replaced
    /// make-before-break. Idempotent — repeated ticks while nothing changed are no-ops.
    /// </summary>
    internal async Task EvaluateLeadershipAsync(CancellationToken ct)
    {
        if (!_leader.IsLeader)
        {
            if (TakeCurrent() is { } lost)
            {
                Retire(lost);
                LogDisconnected();
            }

            return;
        }

        var current = Volatile.Read(ref _current);
        if (current is null)
        {
            await OpenAsync(replacementDriven: false, ct);
            return;
        }

        if (current.Faulted || current.Client.State == AriConnectionState.Faulted)
        {
            if (current.TryMarkFaultLogged())
                LogClientFaulted(_ariOptions!.Application);
            await ReplaceAsync(current, replacementDriven: false, ct);
            return;
        }

        bool due;
        lock (_gate)
            due = _reconnectDue is { } at && _time.GetUtcNow() >= at;
        if (due)
            await ReplaceAsync(current, replacementDriven: true, ct);
    }

    /// <summary>Detaches the current connection (leadership loss, shutdown) and clears the schedule.</summary>
    private Connection? TakeCurrent()
    {
        lock (_gate)
        {
            var current = _current;
            _current = null;
            _reconnectDue = null;
            if (current is not null)
                current.State = ConnectionState.Retiring;
            return current;
        }
    }

    /// <summary>
    /// Make-before-break (design D6): the old connection turns <c>Retiring</c> first, so the
    /// <c>ApplicationReplaced</c> our own new registration sends it is ignored; the new one is
    /// opened and published; only then is the old one retired. If the new one cannot connect the
    /// old one stays current and the next eligible tick retries.
    /// </summary>
    private async Task ReplaceAsync(Connection old, bool replacementDriven, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, old))
                return;
            old.State = ConnectionState.Retiring;
        }

        if (await OpenAsync(replacementDriven, ct) is null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, old))
                    old.State = ConnectionState.Live;
            }

            return;
        }

        Retire(old);
    }

    /// <summary>
    /// Creates a client, subscribes its connection record, then connects (Sdk 2.8.0 delivers events
    /// from inside <c>ConnectAsync</c> into a subject that does not replay), and publishes it as the
    /// current <c>Live</c> connection, acting on a replacement it recorded while <c>Pending</c>.
    /// Returns <see langword="null"/> when it could not connect; the client is then released.
    /// </summary>
    private async Task<Connection?> OpenAsync(bool replacementDriven, CancellationToken ct)
    {
        if (_ariOptions is null)
            return null;

        Connection? connection = null;
        try
        {
            connection = new Connection(this, _factory.Create(_ariOptions));
            connection.Subscription = connection.Client.Subscribe(connection);
            await connection.Client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            LogConnectFailed(ex.Message);
            if (connection is not null)
            {
                lock (_gate)
                    connection.State = ConnectionState.Retiring;
                Retire(connection);
            }

            return null;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (replacementDriven)
                _replacementReconnects.Enqueue(now);
            PruneReplacementWindow(now);

            _current = connection;
            _reconnectDue = null;
            connection.State = ConnectionState.Live;
            LogConnected(_ariOptions.Application);
            if (connection.ReplacedWhilePending)
                ScheduleReconnectLocked(connection);
        }

        return connection;
    }

    /// <summary>Observer callback for <c>ApplicationReplaced</c> (Sdk pump thread), design D4.</summary>
    private void OnReplaced(Connection connection, AriEvent evt)
    {
        if (evt.Application is { Length: > 0 } application
            && !string.Equals(application, _ariOptions?.Application, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            switch (connection.State)
            {
                case ConnectionState.Pending:
                    // Replaced while still connecting: acted on when it is published (OpenAsync).
                    connection.ReplacedWhilePending = true;
                    break;
                case ConnectionState.Live when ReferenceEquals(connection, _current) && !connection.Replaced:
                    ScheduleReconnectLocked(connection);
                    break;
                default:
                    // Retiring: our own reconnect registered the app on its successor — not a replacement.
                    break;
            }
        }
    }

    /// <summary>Warning + counter + the D5 backoff/cap schedule for one replacement. Caller holds <see cref="_gate"/>.</summary>
    private void ScheduleReconnectLocked(Connection connection)
    {
        connection.Replaced = true;
        _appReplaced.Add(1);

        var now = _time.GetUtcNow();
        PruneReplacementWindow(now);
        var nth = _replacementReconnects.Count + 1;
        var application = _ariOptions!.Application;
        if (nth <= MaxReplacementReconnects)
        {
            var delay = FirstReplacementDelay * (1 << (nth - 1));
            _reconnectDue = now + delay;
            LogAppReplaced(application, delay.TotalSeconds);
            return;
        }

        // Capped: wait until the oldest reconnect in the window ages out; never stop while leader.
        var due = _replacementReconnects.Peek() + ReplacementWindow;
        _reconnectDue = due;
        LogAppReplaced(application, (due - now).TotalSeconds);
        if (_lastCapError is not { } last || now - last >= ReplacementWindow)
        {
            _lastCapError = now;
            LogReplacementCapped(application, nth);
        }
    }

    private void PruneReplacementWindow(DateTimeOffset now)
    {
        while (_replacementReconnects.TryPeek(out var oldest) && now - oldest >= ReplacementWindow)
            _replacementReconnects.Dequeue();
    }

    /// <summary>Starts a tracked retirement off the caller's path (the tick loop never waits on a handler).</summary>
    private void Retire(Connection connection)
    {
        var retirement = RetireAsync(connection);
        _retirements.TryAdd(retirement, 0);
        _ = retirement.ContinueWith(
            static (done, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(done, out _),
            _retirements, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Design D6: close the event WebSocket at once (releases the app, cancels the Sdk's own
    /// reconnect loop), wait for the handlers started on this connection (at most
    /// <see cref="DrainDeadline"/>), then release the client and its REST <c>HttpClient</c>.
    /// </summary>
    private async Task RetireAsync(Connection connection)
    {
        // Best-effort teardown: any failure is logged and the next half still runs.
        try { connection.Subscription?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LogTeardownWarning(ex.Message); }

        try { await connection.Client.DisconnectAsync(_stoppingToken); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LogTeardownWarning(ex.Message); }

        if (!await connection.DrainAsync(DrainDeadline, _time))
            LogDrainDeadline(connection.InFlight);

        try { await connection.Client.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LogTeardownWarning(ex.Message); }
    }

    /// <summary>
    /// True only for a <see cref="StasisStartEvent"/> whose first Stasis arg is
    /// the inbound phase marker (<c>Stasis(verbara,inbound,${EXTEN})</c>).
    /// </summary>
    internal static bool IsInboundStart(AriEvent evt) =>
        evt is StasisStartEvent { Args: { Length: > 0 } args }
        && string.Equals(args[0], InboundPhase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only for a <see cref="StasisStartEvent"/> of the campaign phase
    /// (<c>Stasis(verbara,campaign,${DIALER_CAMPAIGN_ID})</c>, dialer-places-calls-end-to-end design D11).
    /// </summary>
    internal static bool IsCampaignStart(AriEvent evt) =>
        evt is StasisStartEvent { Args: { Length: > 0 } args }
        && string.Equals(args[0], CampaignPhase, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Continues an answered campaign leg into its campaign's target queue (dialer-places-calls-end-to-end design
    /// D11): the tenant from the leg's <c>TENANT_ID</c>, the campaign id from the second Stasis argument, the
    /// campaign loaded tenant-scoped (another tenant's id never reaches this tenant's queue), its
    /// <c>TargetQueueName</c> looked up by exact name within the tenant, then <c>QUEUE_NAME</c> =
    /// <c>{tenantId}-{queueName}</c> and a continue into <c>[stasis-queue]</c>. The leg is already up (an
    /// originate's dialplan runs only after the answer), so it is not answered, and no reason hint is captured (a
    /// campaign call has no DID; its typification comes from the <c>campaignId</c> metadata, design D10). Every step
    /// it cannot resolve fails closed: one Warning naming the tenant, the campaign and the reason, then a hangup.
    /// Never throws.
    /// </summary>
    internal async Task HandleCampaignStartAsync(IAriClient client, StasisStartEvent evt, CancellationToken ct)
    {
        var channelId = evt.Channel?.Id;
        if (string.IsNullOrEmpty(channelId))
        {
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

            var rawCampaignId = evt.Args is { Length: > 1 } args ? args[1] : null;
            if (!long.TryParse(rawCampaignId, NumberStyles.None, CultureInfo.InvariantCulture, out var campaignId))
            {
                await RefuseCampaignAsync(client, channelId, tenant, rawCampaignId ?? "",
                    "the campaign id is missing or not a number", ct);
                return;
            }

            var campaignText = campaignId.ToString(CultureInfo.InvariantCulture);
            if (_campaigns is null)
            {
                await RefuseCampaignAsync(client, channelId, tenant, campaignText,
                    "the dialer is not configured on this host (no campaign store)", ct);
                return;
            }

            var campaign = await _campaigns.GetCampaignAsync(tenant, campaignId, ct);
            if (campaign is null)
            {
                await RefuseCampaignAsync(client, channelId, tenant, campaignText,
                    "the tenant has no such campaign", ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(campaign.TargetQueueName))
            {
                await RefuseCampaignAsync(client, channelId, tenant, campaignText,
                    "the campaign has no target queue", ct);
                return;
            }

            // A tenant-scoped lookup by exact name, never a page of ListAsync: a target sorting past the first page
            // would otherwise be hung up.
            var queue = await _queues.GetByNameAsync(new TenantId(tenant), campaign.TargetQueueName, ct);
            if (queue is null)
            {
                await RefuseCampaignAsync(client, channelId, tenant, campaignText,
                    $"the target queue '{campaign.TargetQueueName}' is not the tenant's", ct);
                return;
            }

            // The realtime sync publishes only active queues (PlatformDesiredStateProvider), so Queue() would name a
            // realtime queue Asterisk does not have.
            if (!queue.IsActive)
            {
                await RefuseCampaignAsync(client, channelId, tenant, campaignText,
                    $"the target queue '{queue.Name}' is inactive", ct);
                return;
            }

            var asteriskQueue = $"{tenant}-{queue.Name}";
            await client.Channels.SetVariableAsync(channelId, QueueNameVariable, asteriskQueue, ct);
            await client.Channels.ContinueAsync(
                channelId, context: StasisQueueContext, extension: QueueEntryExtension, priority: 1, label: null, ct);
            LogCampaignRoutedToQueue(channelId, tenant, campaignText, asteriskQueue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogCampaignHandleError(channelId, ex.Message);
            await SafeHangupAsync(client, channelId, ct);
        }
    }

    private async Task RefuseCampaignAsync(
        IAriClient client, string channelId, string tenant, string campaignId, string reason, CancellationToken ct)
    {
        LogCampaignRefused(channelId, tenant, campaignId, reason);
        await SafeHangupAsync(client, channelId, ct);
    }

    /// <summary>Observer callback — dispatched on the connection's ARI WS thread.</summary>
    private void OnEvent(Connection connection, AriEvent value)
    {
        if (value is ApplicationReplacedEvent)
        {
            OnReplaced(connection, value);
            return;
        }

        // Both phases share the per-record dispatch (ADR-0044 design D4): the campaign phase (design D11) runs on
        // the client of the connection its StasisStart arrived on, counted in that connection's drain.
        var inbound = IsInboundStart(value);
        if (!inbound && !IsCampaignStart(value))
            return;

        // Fire-and-forget with its own guard (neither phase handler throws), on the client of
        // the connection the call arrived on — never a newer one — and counted so a retiring
        // connection keeps its REST client until the handler is done.
        connection.HandlerStarted();
        _ = RunHandlerAsync(connection, (StasisStartEvent)value, inbound);
    }

    private async Task RunHandlerAsync(Connection connection, StasisStartEvent evt, bool inbound)
    {
        try
        {
            if (inbound)
                await HandleStasisStartAsync(connection.Client, evt, _stoppingToken);
            else
                await HandleCampaignStartAsync(connection.Client, evt, _stoppingToken);
        }
        finally
        {
            connection.HandlerEnded();
        }
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

            // Typification P1 implicit voice capture: resolve the most-specific reason hint for the
            // DID/queue and push it to the VERBARA_REASON channel var so VoiceConversationBridge can
            // stamp Conversation.Metadata["reasonPath"] when it creates the call's Conversation. A
            // missing hint simply skips — voice routing never depends on a typification hint existing.
            //
            // This OPTIONAL best-effort capture is wrapped in its own try/catch so a transient
            // store/ARI failure during reason resolution can NEVER hang up an otherwise-routable
            // caller: it logs and continues straight to ContinueAsync. Mirrors the defensive pattern
            // in VoiceConversationBridge.ResolveReasonFromChannelAsync / ReadTenantAsync.
            try
            {
                var hint = await _reasonHints.ResolveAsync(new TenantId(tenant), did, route.QueueId, ChannelType.Voice, ct);
                if (hint is not null && !string.IsNullOrEmpty(hint.ReasonPath))
                    await client.Channels.SetVariableAsync(channelId, ReasonVariable, hint.ReasonPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception reasonEx)
            {
                LogReasonHintFailed(channelId, tenant, reasonEx.Message);
            }

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
    /// Marks the current ARI connection as faulted so the next leadership tick replaces it. The
    /// observer's OnError/OnCompleted take the same path; Sdk 2.8.0 never calls them in production
    /// (it reconnects on its own), so the tick's <see cref="AriConnectionState.Faulted"/> check is
    /// the path that matters. Available to tests to drive the replacement deterministically.
    /// </summary>
    internal void SignalConnectionFault() => Volatile.Read(ref _current)?.MarkFaulted();

    private enum ConnectionState
    {
        /// <summary>Subscribed, connect in progress.</summary>
        Pending,

        /// <summary>The consumer's current connection.</summary>
        Live,

        /// <summary>Being replaced or retired: its replacements are ignored.</summary>
        Retiring,
    }

    /// <summary>
    /// One ARI connection: its client, subscription, state (moved under <see cref="_gate"/>) and the
    /// count of call handlers running on its client. It is also the connection's own observer.
    /// </summary>
    private sealed class Connection(StasisInboundConsumer owner, IAriClient client) : IObserver<AriEvent>
    {
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private volatile bool _draining;
        private volatile bool _faulted;
        private int _faultLogged;

        public IAriClient Client { get; } = client;

        public IDisposable? Subscription { get; set; }

        public ConnectionState State { get; set; } = ConnectionState.Pending;

        /// <summary>Its app was replaced while <see cref="ConnectionState.Pending"/>.</summary>
        public bool ReplacedWhilePending { get; set; }

        /// <summary>A replacement was acted on; the reconnect is scheduled.</summary>
        public bool Replaced { get; set; }

        public bool Faulted => _faulted;

        public int InFlight => Volatile.Read(ref _inFlight);

        public void MarkFaulted() => _faulted = true;

        public bool TryMarkFaultLogged() => Interlocked.Exchange(ref _faultLogged, 1) == 0;

        public void HandlerStarted() => Interlocked.Increment(ref _inFlight);

        public void HandlerEnded()
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && _draining)
                _drained.TrySetResult();
        }

        /// <summary>True when every handler finished; false when <paramref name="deadline"/> passed first.</summary>
        public async Task<bool> DrainAsync(TimeSpan deadline, TimeProvider time)
        {
            _draining = true;
            if (InFlight == 0)
                _drained.TrySetResult();
            if (_drained.Task.IsCompleted)
                return true;

            using var timer = new CancellationTokenSource();
            var expired = Task.Delay(deadline, time, timer.Token);
            var first = await Task.WhenAny(_drained.Task, expired);
            await timer.CancelAsync();
            return first == _drained.Task;
        }

        public void OnNext(AriEvent value) => owner.OnEvent(this, value);

        // A WebSocket error or graceful completion means this connection is dead — flag it so
        // EvaluateLeadershipAsync replaces it if it is the current one (never connected-but-deaf).
        public void OnError(Exception error) => MarkFaulted();

        public void OnCompleted() => MarkFaulted();
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
        Message = "[STASIS] Stasis app '{Application}' was taken by another ARI client — re-registering in {DelaySeconds} s.")]
    private partial void LogAppReplaced(string application, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[STASIS] Stasis app '{Application}' replaced {Count} times in 10 min — re-registration capped; another ARI client registers the same name (check Asterisk:Ari:NodeApplication, a second leader, or an external ARI tool).")]
    private partial void LogReplacementCapped(string application, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] ARI client for app '{Application}' is faulted (its own reconnects ended) — replacing the connection.")]
    private partial void LogClientFaulted(string application);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] {InFlight} call handler(s) still running when the retired ARI connection's 30-s drain ended — releasing it.")]
    private partial void LogDrainDeadline(int inFlight);

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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}): reason-hint capture failed — routing call anyway: {Reason}")]
    private partial void LogReasonHintFailed(string channelId, string tenantId, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}, DID {Did}) → queue '{Queue}'.")]
    private partial void LogRoutedToQueue(string channelId, string tenantId, string did, string queue);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[STASIS] Channel {ChannelId}: error handling inbound call: {Reason}")]
    private partial void LogHandleError(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}, campaign {CampaignId}): {Reason} — hanging up.")]
    private partial void LogCampaignRefused(string channelId, string tenantId, string campaignId, string reason);

    // The lab's P8 reads this line (dialer-places-calls-end-to-end task 4.2): keep its shape.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "[STASIS] Channel {ChannelId} (tenant {TenantId}, campaign {CampaignId}) → queue '{Queue}'")]
    private partial void LogCampaignRoutedToQueue(string channelId, string tenantId, string campaignId, string queue);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[STASIS] Channel {ChannelId}: error handling answered campaign call: {Reason}")]
    private partial void LogCampaignHandleError(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[STASIS] Channel {ChannelId}: hangup failed: {Reason}")]
    private partial void LogHangupFailed(string channelId, string reason);
}
