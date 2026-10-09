using Verbara.Platform.Api.Health;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Runs the licensed-agent daily close every 15 minutes and the fixed 15-month retention purge once a day
/// (licensed-agent-metering, design D7/D8). No leader lease: every chain is closed under its head lock, so a
/// second replica writes nothing (design D7).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Everything external is resolved on first use, never in <c>Host.StartAsync</c>: with no Postgres
/// storage (<see cref="ILicenseAgentDailyClose"/> not registered — the OpenAPI export, design-time tools, the
/// in-memory host) the worker returns at its first tick, and the day-zone check never runs.</item>
/// <item>The first tick reads <c>Licensing:Metering:DayZone</c> (UTC when unset). A zone that differs from
/// the one the deployment chain was anchored with stops the worker before anything is anchored or closed:
/// the error naming both zones is logged as critical and the worker's heartbeat is reported unhealthy at once
/// (<see cref="BackgroundServiceHealthCheck"/>), so a rollout with the wrong zone never becomes ready.</item>
/// <item>Nothing is anchored or closed until the host's licence load attempt has completed
/// (<see cref="ILicenseIdSource.LoadCompleted"/>): the worker starts before Pro's licence validation service
/// has read the <c>.lic</c>, and anchoring then would write the deployment chain's null form, followed a tick
/// later by a spurious <c>chain_reanchored</c>. While it waits the worker logs once, keeps its heartbeat
/// healthy (a transient start-up state, not a fault) and retries every
/// <see cref="LicenceLoadRetryInterval"/>. Without a licence-id seam (hosts with no licence service) there is
/// nothing to wait for.</item>
/// <item>Licence validity is never consulted: once the load attempt has completed, the close runs under a
/// missing, expired or grace licence, a missing one anchoring with the null form (verbara-meta/ADR-0020 §6).</item>
/// </list>
/// </remarks>
internal sealed partial class LicenseAgentDailyCloseWorker : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    /// <summary>How soon the worker retries while the licence load attempt has not completed yet.</summary>
    internal static readonly TimeSpan LicenceLoadRetryInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly ILogger<LicenseAgentDailyCloseWorker> _logger;
    private readonly TimeProvider _time;

    private ILicenseAgentDailyClose? _close;
    private TimeZoneInfo? _zone;
    private DateOnly? _lastPurgeDay;
    private bool _prepared;
    private bool _waitingForLicence;
    private bool _loggedLicenceWait;

    public LicenseAgentDailyCloseWorker(
        IServiceProvider services,
        IConfiguration configuration,
        IServiceHeartbeat heartbeat,
        ILogger<LicenseAgentDailyCloseWorker> logger,
        TimeProvider? time = null)
    {
        _services = services;
        _configuration = configuration;
        _heartbeat = heartbeat;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether the worker stopped on a configuration error (an unknown or changed day zone).</summary>
    internal bool Stopped { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await TickAsync(stoppingToken).ConfigureAwait(false))
                    return;
                await Task.Delay(_waitingForLicence ? LicenceLoadRetryInterval : TickInterval, _time, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <summary>One tick; returns false when the worker stops (no Postgres storage, or a configuration error).</summary>
    internal async Task<bool> TickAsync(CancellationToken ct)
    {
        if (_close is null)
        {
            _close = _services.GetService<ILicenseAgentDailyClose>();
            if (_close is null)
            {
                LogNoPostgres(_logger);
                return false;
            }
        }

        // Anchoring before the licence has loaded would stamp the null form and re-anchor a tick later. Once the
        // first-tick step has run, later licence changes are the ledger's re-anchor, not a reason to wait.
        _waitingForLicence = !_prepared && _services.GetService<ILicenseIdSource>() is { LoadCompleted: false };
        if (_waitingForLicence)
        {
            if (!_loggedLicenceWait)
            {
                _loggedLicenceWait = true;
                LogWaitingForLicence(_logger);
            }

            _heartbeat.RecordTick(nameof(LicenseAgentDailyCloseWorker), TickInterval);
            return true;
        }

        if (!_prepared)
        {
            var zoneId = _configuration[LicenseAgentDayZone.ConfigurationKey] is { Length: > 0 } configured
                ? configured
                : LicenseAgentDayZone.DefaultZone;
            try
            {
                _zone = LicenseAgentDayZone.Resolve(zoneId);
                var anchored = await _close.PrepareAsync(zoneId, ct).ConfigureAwait(false);
                LogPrepared(_logger, zoneId, anchored);
                _prepared = true;
            }
            catch (Exception ex) when (ex is LicenseAgentDayZoneMismatchException or ArgumentException)
            {
                Stop(ex);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure (database unreachable): try the first-tick step again next tick.
                LogTickFailed(_logger, ex);
                _heartbeat.RecordTick(nameof(LicenseAgentDailyCloseWorker), TickInterval);
                return true;
            }
        }

        try
        {
            var now = _time.GetUtcNow();
            var result = await _close.CloseAsync(_zone!, now, ct).ConfigureAwait(false);
            if (result.DaysClosed > 0 || result.Corrections > 0 || result.ChainsAnchored > 0 || result.ChainsFailed > 0)
                LogClosed(_logger, result.ChainsAnchored, result.DaysClosed, result.Corrections, result.ChainsFailed);

            var today = LicenseAgentDayZone.DayOf(now, _zone!);
            if (_lastPurgeDay != today && _services.GetService<ILicenseAgentRetentionPurge>() is { } purge)
            {
                var purged = await purge.PurgeAsync(_zone!, now, ct).ConfigureAwait(false);
                _lastPurgeDay = today;
                if (purged > 0)
                    LogPurged(_logger, purged);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTickFailed(_logger, ex);
        }

        _heartbeat.RecordTick(nameof(LicenseAgentDailyCloseWorker), TickInterval);
        return true;
    }

    private void Stop(Exception ex)
    {
        Stopped = true;
        LogStopped(_logger, ex, ex.Message);
        // An expected interval of zero makes the heartbeat stale at once: the readiness check reports the
        // stopped close now instead of after three missed ticks.
        _heartbeat.RecordTick(nameof(LicenseAgentDailyCloseWorker), TimeSpan.Zero);
    }

    [LoggerMessage(EventId = 7521, Level = LogLevel.Information,
        Message = "Licensed-agent daily close not started: no Postgres storage is configured.")]
    private static partial void LogNoPostgres(ILogger logger);

    [LoggerMessage(EventId = 7522, Level = LogLevel.Information,
        Message = "Licensed-agent daily close ready in day zone {DayZone}; {Anchored} chain(s) anchored.")]
    private static partial void LogPrepared(ILogger logger, string dayZone, int anchored);

    [LoggerMessage(EventId = 7523, Level = LogLevel.Critical,
        Message = "Licensed-agent daily close stopped: {Reason}")]
    private static partial void LogStopped(ILogger logger, Exception exception, string reason);

    [LoggerMessage(EventId = 7524, Level = LogLevel.Information,
        Message = "Licensed-agent daily close: {Anchored} chain(s) anchored, {Days} day row(s) closed, {Corrections} correction(s), {Failed} chain(s) failed.")]
    private static partial void LogClosed(ILogger logger, int anchored, int days, int corrections, int failed);

    [LoggerMessage(EventId = 7525, Level = LogLevel.Information,
        Message = "Licensed-agent retention purge removed rows older than 15 months from {Chains} chain(s).")]
    private static partial void LogPurged(ILogger logger, int chains);

    [LoggerMessage(EventId = 7527, Level = LogLevel.Information,
        Message = "Licensed-agent daily close waiting for the licence to load before anchoring any chain.")]
    private static partial void LogWaitingForLicence(ILogger logger);

    [LoggerMessage(EventId = 7526, Level = LogLevel.Error,
        Message = "Licensed-agent daily close tick failed; the next tick retries.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
