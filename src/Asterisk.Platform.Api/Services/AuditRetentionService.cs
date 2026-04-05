using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class AuditRetentionService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);
    private const int DefaultRetentionMonths = 12;

    private readonly IAuditStore _auditStore;
    private readonly IClock _clock;
    private readonly ILogger<AuditRetentionService> _logger;

    public AuditRetentionService(IAuditStore auditStore, IClock clock,
        ILogger<AuditRetentionService> logger)
    {
        _auditStore = auditStore;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);

                var cutoff = _clock.UtcNow.AddMonths(-DefaultRetentionMonths);
                // Pass a wildcard tenant to purge across all tenants
                var deleted = await _auditStore.DeleteOlderThanAsync(
                    new TenantId("*"), cutoff, stoppingToken);
                if (deleted > 0)
                    LogPurged(_logger, deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPurgeError(_logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired audit entries")]
    private static partial void LogPurged(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit retention purge failed")]
    private static partial void LogPurgeError(ILogger logger, Exception ex);
}
