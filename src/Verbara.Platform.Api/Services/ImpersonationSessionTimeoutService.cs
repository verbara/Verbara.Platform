using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// R5.2 PB.2 / C.7 — periodic sweep that revokes impersonation sessions whose
/// elapsed time exceeds the per-tenant
/// <see cref="TenantAuthConfig.ImpersonationAutoTimeoutMinutes"/> budget.
///
/// Lives under <c>Verbara.Platform.Api.Services</c> rather than
/// <c>Verbara.Platform.Core.Impersonation</c> because <c>Core</c> intentionally
/// does not reference the <c>Identity</c> + <c>Audit</c> projects (which the
/// sweep needs for the per-tenant settings + audit emission). The
/// <see cref="IImpersonationSessionStore"/> contract + session model still live
/// in Core so any composition root can wire alternative implementations
/// (Redis / Postgres) without taking a dependency on the worker.
///
/// Idempotent + restart-tolerant by design: the per-session
/// <c>RevokeAsync</c> call short-circuits when the session is already closed,
/// so a duplicate sweep (or a manual revoke racing the periodic tick) cannot
/// double-emit audit events for the same session.
/// </summary>
public sealed partial class ImpersonationSessionTimeoutService : BackgroundService
{
    /// <summary>Default sweep cadence — every 60 s.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    private readonly IImpersonationSessionStore _store;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly IAuditService _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ImpersonationSessionTimeoutService> _logger;
    private readonly TimeSpan _sweepInterval;

    public ImpersonationSessionTimeoutService(
        IImpersonationSessionStore store,
        ITenantAuthConfigStore authConfigStore,
        IAuditService audit,
        ILogger<ImpersonationSessionTimeoutService> logger,
        TimeProvider? clock = null,
        TimeSpan? sweepInterval = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authConfigStore);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _authConfigStore = authConfigStore;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_sweepInterval, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogSweepFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(ImpersonationSessionTimeoutService), fatalEx.Message, fatalEx);
            throw;
        }
    }

    /// <summary>
    /// Single sweep pass. Public so tests can drive the loop deterministically
    /// (see <c>SessionTimeoutServiceTests</c>) without depending on
    /// <see cref="PeriodicTimer"/> ticks.
    /// </summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var active = await _store.GetActiveAsync(ct).ConfigureAwait(false);
        if (active.Count == 0)
            return;

        var now = _clock.GetUtcNow();

        foreach (var session in active)
        {
            ct.ThrowIfCancellationRequested();

            var config = await _authConfigStore.GetAsync(session.ActorTenantId, ct).ConfigureAwait(false);
            var timeoutMinutes = config?.ImpersonationAutoTimeoutMinutes
                ?? new TenantAuthConfig { TenantId = session.ActorTenantId }.ImpersonationAutoTimeoutMinutes;

            if (timeoutMinutes <= 0)
                continue; // disabled by tenant policy

            var elapsed = now - session.StartedAt;
            if (elapsed.TotalMinutes < timeoutMinutes)
                continue;

            var revoked = await _store.RevokeAsync(
                session.Id,
                ImpersonationSessionStatus.AutoTimedOut,
                reason: "auto_timeout",
                endedAt: now,
                ct).ConfigureAwait(false);

            if (!revoked)
                continue; // raced — manual revoke or completion already closed it

            try
            {
                await _audit.RecordAsync(
                    new TenantId(session.ActorTenantId),
                    category: "auth",
                    action: "impersonation.session.auto_timeout",
                    severity: "warning",
                    actorId: session.ActorUserId,
                    actorType: "system",
                    targetId: session.Id,
                    targetType: "ImpersonationSession",
                    metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["actor_tenant_id"] = session.ActorTenantId,
                        ["target_tenant_id"] = session.TargetTenantId,
                        ["timeout_minutes"] = timeoutMinutes
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["elapsed_minutes"] = ((int)elapsed.TotalMinutes)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["read_only"] = session.ReadOnly ? "true" : "false",
                    },
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogAuditWriteFailed(session.Id, ex);
            }
        }
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Error,
        Message = "Impersonation timeout sweep failed.")]
    partial void LogSweepFailed(Exception ex);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning,
        Message = "Impersonation auto-timeout revoke succeeded but audit emission failed for session {SessionId}.")]
    partial void LogAuditWriteFailed(string sessionId, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
