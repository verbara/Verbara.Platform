using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

internal sealed partial class AuditRetentionService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each
    /// audit-purge call. Uses DB-heavy budget (long timeout, 1 retry) — circuit-open skips
    /// the current cycle; the next daily tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.audit-retention";

    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);
    private const int DefaultRetentionMonths = 12;

    private readonly IAuditStore _auditStore;
    private readonly IClock _clock;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<AuditRetentionService> _logger;

    public AuditRetentionService(
        IAuditStore auditStore,
        IClock clock,
        ILogger<AuditRetentionService> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _auditStore = auditStore;
        _clock = clock;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _policy.ExecuteAsync(
                    ResiliencePolicyKey,
                    async innerCt =>
                    {
                        await RunPurgeAsync(innerCt);
                        return 0;
                    },
                    stoppingToken);
            }
            catch (CircuitBreakerOpenException)
            {
                LogCircuitOpen(_logger);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogPurgeError(_logger, ex);
            }
        }
    }

    internal async Task RunPurgeAsync(CancellationToken ct)
    {
        var cutoff = _clock.UtcNow.AddMonths(-DefaultRetentionMonths);
        // Pass a wildcard tenant to purge across all tenants
        var deleted = await _auditStore.DeleteOlderThanAsync(
            new TenantId("*"), cutoff, ct);
        if (deleted > 0)
            LogPurged(_logger, deleted);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired audit entries")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit retention purge failed")]
    private static partial void LogPurgeError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.audit-retention — skipping cycle")]
    private static partial void LogCircuitOpen(ILogger logger);
}
