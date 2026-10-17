using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Storage.Postgres.Realtime;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Storage.Postgres;

namespace Verbara.Platform.Api.Services;

/// <summary>#385 — a fingerprint of <c>ps_registrations</c>, or null when it cannot be read here (public so tests can substitute it).</summary>
public interface ITrunkRegistrationFingerprintSource
{
    Task<string?> GetFingerprintAsync(CancellationToken ct);
}

/// <summary>
/// Reads the fingerprint from the Asterisk Realtime database Platform already writes
/// (<see cref="RealtimeDbContext"/>); answers null when no Realtime database is configured.
/// </summary>
internal sealed class RealtimeTrunkRegistrationFingerprintSource : ITrunkRegistrationFingerprintSource
{
    private readonly RealtimeDbContext? _db;

    public RealtimeTrunkRegistrationFingerprintSource(RealtimeDbContext? db) => _db = db;

    public async Task<string?> GetFingerprintAsync(CancellationToken ct) =>
        _db is null ? null : await TrunkRegistrationFingerprint.ReadAsync(_db.DataSource, ct).ConfigureAwait(false);
}

/// <summary>
/// #385 — keeps Asterisk's outbound registrations in step with <c>ps_registrations</c>. Asterisk reads that
/// table only when <c>res_pjsip_outbound_registration</c> loads or reloads (measured in the lab: a row added
/// after boot sends no REGISTER; <c>ModuleLoad reload res_pjsip_outbound_registration.so</c> sends one
/// REGISTER for the new row only, and an unregister for a deleted one).
/// </summary>
/// <remarks>
/// <para><b>Signal path (not leader-gated).</b> A trunk write raises <see cref="TrunkRegistrationReloadSignal"/>
/// on the pod that served it; after <see cref="DefaultDebounce"/> of quiet this worker sends one module reload
/// over that pod's own AMI connection. Asterisk must answer <c>Success</c>; on an error answer, an exception,
/// a missing or disconnected server, the signal stays dirty and the reload is retried with a backoff of
/// 1, 2, 4 … 30 s. Nothing here throws into the host.</para>
/// <para><b>Convergence pass (leader-gated).</b> Every <see cref="RealtimeOptions.ReconcilerIntervalSeconds"/>
/// the AMI-owner leader compares the <c>ps_registrations</c> fingerprint with the one recorded at the last
/// reload Asterisk accepted, and reloads when they differ (or none was recorded yet). It catches writes whose
/// signal was lost, e.g. a pod that died before reloading.</para>
/// <para><b>Deferred seam.</b> The primary AMI server is resolved at use time, never at construction or host
/// start (same accessor as <see cref="TrunkConnectivityTester"/>).</para>
/// </remarks>
internal sealed partial class TrunkRegistrationReloadWorker : BackgroundService
{
    /// <summary>The module whose reload makes Asterisk read <c>ps_registrations</c> again (the name needs <c>.so</c>).</summary>
    internal const string ModuleName = "res_pjsip_outbound_registration.so";

    /// <summary>Quiet time after the last signal before the reload is sent.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bound on consecutive debounce windows, so a constant stream of writes still reloads.</summary>
    private const int MaxDebounceWindows = 10;

    private readonly TrunkRegistrationReloadSignal _signal;
    private readonly VerbaraServerPool _serverPool;
    private readonly IClusterLeader _leader;
    private readonly ITrunkRegistrationFingerprintSource _fingerprints;
    private readonly ILogger<TrunkRegistrationReloadWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _debounce;
    private volatile string? _lastFingerprint;

    public TrunkRegistrationReloadWorker(
        TrunkRegistrationReloadSignal signal,
        VerbaraServerPool serverPool,
        [FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader leader,
        ITrunkRegistrationFingerprintSource fingerprints,
        IOptions<RealtimeOptions> options,
        ILogger<TrunkRegistrationReloadWorker> logger,
        TimeProvider? clock = null,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(serverPool);
        ArgumentNullException.ThrowIfNull(leader);
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _signal = signal;
        _serverPool = serverPool;
        _leader = leader;
        _fingerprints = fingerprints;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.ReconcilerIntervalSeconds));
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var nextTick = _clock.GetUtcNow() + _interval;
            var backoff = TimeSpan.Zero;
            while (!stoppingToken.IsCancellationRequested)
            {
                var wake = _signal.WakeTask;
                if (_signal.IsDirty)
                {
                    await DebounceAsync(stoppingToken).ConfigureAwait(false);
                    if (await ReloadOnceAsync(stoppingToken).ConfigureAwait(false))
                    {
                        backoff = TimeSpan.Zero;
                        continue;
                    }

                    backoff = backoff == TimeSpan.Zero
                        ? TimeSpan.FromSeconds(1)
                        : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                    LogRetryScheduled(backoff.TotalSeconds);
                    await Task.Delay(backoff, _clock, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var untilTick = nextTick - _clock.GetUtcNow();
                if (untilTick > TimeSpan.Zero
                    && await WaitForWakeAsync(wake, untilTick, stoppingToken).ConfigureAwait(false))
                {
                    continue;   // signalled — handled at the top of the loop
                }

                if (_clock.GetUtcNow() >= nextTick)
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                    nextTick = _clock.GetUtcNow() + _interval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(TrunkRegistrationReloadWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    /// <summary>
    /// Signal path: when the registrations are dirty, sends one module reload and answers whether Asterisk
    /// accepted it (true as well when nothing was pending). On failure the signal stays dirty. Not
    /// leader-gated. Public so tests can drive it without the loop.
    /// </summary>
    public async Task<bool> ReloadOnceAsync(CancellationToken ct)
    {
        if (!_signal.TryTake())
            return true;

        // Read before the reload: whatever the table holds now, the reload applies at least that.
        var fingerprint = await TryReadFingerprintAsync(ct).ConfigureAwait(false);
        if (await SendReloadAsync("trunk-change", ct).ConfigureAwait(false))
        {
            if (fingerprint is not null)
                _lastFingerprint = fingerprint;
            return true;
        }

        _signal.Restore();
        return false;
    }

    /// <summary>
    /// Convergence pass: on the AMI-owner leader only, reloads when the <c>ps_registrations</c> fingerprint
    /// differs from the one recorded at the last accepted reload. Never throws (except on cancellation).
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_leader.IsLeader)
            return;

        var fingerprint = await TryReadFingerprintAsync(ct).ConfigureAwait(false);
        if (fingerprint is null || string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
            return;

        if (await SendReloadAsync("drift", ct).ConfigureAwait(false))
            _lastFingerprint = fingerprint;
    }

    private async Task<bool> SendReloadAsync(string reason, CancellationToken ct)
    {
        var server = _serverPool.GetServer("primary");
        if (server is null)
        {
            LogNoServer(reason);
            return false;
        }

        var connection = server.Connection;
        if (connection.State != AmiConnectionState.Connected)
        {
            LogNotConnected(reason, connection.State.ToString());
            return false;
        }

        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var response = await connection.SendActionAsync(
                new ModuleLoadAction { Module = ModuleName, LoadType = "reload" }, linked.Token).ConfigureAwait(false);

            if (!string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase))
            {
                LogReloadRefused(reason, response.Response ?? "", response.Message ?? "");
                return false;
            }

            LogReloaded(reason, response.Message ?? "");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Shutdown cancellation propagates; anything else (AMI down, timeout, a bad response) keeps the flag dirty.
            LogReloadFailed(reason, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    private async Task<string?> TryReadFingerprintAsync(CancellationToken ct)
    {
        try
        {
            return await _fingerprints.GetFingerprintAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFingerprintFailed(ex.Message);
            return null;
        }
    }

    /// <summary>Waits until no signal arrived for one debounce window (bounded by <see cref="MaxDebounceWindows"/>).</summary>
    private async Task DebounceAsync(CancellationToken ct)
    {
        for (var i = 0; i < MaxDebounceWindows; i++)
        {
            if (!await WaitForWakeAsync(_signal.WakeTask, _debounce, ct).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>True when <paramref name="wake"/> completed before <paramref name="timeout"/> elapsed.</summary>
    private async Task<bool> WaitForWakeAsync(Task wake, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, _clock, cts.Token);
        var first = await Task.WhenAny(wake, delay).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return first == wake;
    }

    [LoggerMessage(EventId = 9170, Level = LogLevel.Information,
        Message = "[TRUNK-REG] Reloaded res_pjsip_outbound_registration ({Reason}): {Message}")]
    private partial void LogReloaded(string reason, string message);

    [LoggerMessage(EventId = 9171, Level = LogLevel.Warning,
        Message = "[TRUNK-REG] Asterisk refused the registration reload ({Reason}): Response={Response} Message={Message}")]
    private partial void LogReloadRefused(string reason, string response, string message);

    [LoggerMessage(EventId = 9172, Level = LogLevel.Warning,
        Message = "[TRUNK-REG] Registration reload ({Reason}) failed: {ExceptionType}: {Message}")]
    private partial void LogReloadFailed(string reason, string exceptionType, string message);

    [LoggerMessage(EventId = 9173, Level = LogLevel.Warning,
        Message = "[TRUNK-REG] Registration reload ({Reason}) deferred: no primary Asterisk server in the pool")]
    private partial void LogNoServer(string reason);

    [LoggerMessage(EventId = 9174, Level = LogLevel.Warning,
        Message = "[TRUNK-REG] Registration reload ({Reason}) deferred: AMI connection is {State}")]
    private partial void LogNotConnected(string reason, string state);

    [LoggerMessage(EventId = 9175, Level = LogLevel.Information,
        Message = "[TRUNK-REG] Registration reload pending; retrying in {Seconds} s")]
    private partial void LogRetryScheduled(double seconds);

    [LoggerMessage(EventId = 9176, Level = LogLevel.Warning,
        Message = "[TRUNK-REG] Could not read the ps_registrations fingerprint: {Message}")]
    private partial void LogFingerprintFailed(string message);

    [LoggerMessage(EventId = 9177, Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
