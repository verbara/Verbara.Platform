using Asterisk.Platform.Bot;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class BotAnalyticsPersistenceService : BackgroundService
{
    private readonly BotAnalyticsCollector _collector;
    private readonly IBotAnalyticsStore _store;
    private readonly ILogger<BotAnalyticsPersistenceService> _logger;

    public BotAnalyticsPersistenceService(
        BotAnalyticsCollector collector,
        IBotAnalyticsStore store,
        ILogger<BotAnalyticsPersistenceService> logger)
    {
        _collector = collector;
        _store = store;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _collector.Events.Subscribe(async evt =>
        {
            try
            {
                var record = new BotAnalyticsRecord
                {
                    EventType = evt.Type.ToString(),
                    BotId = evt.BotId.Value,
                    ConversationId = evt.ConversationId.Value,
                    TurnCount = evt.TurnCount ?? 0,
                    HandoffReason = evt.HandoffReason,
                    CreatedAt = evt.OccurredAt,
                };

                await _store.RecordEventAsync(evt.TenantId, record, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogPersistError(ex);
            }
        });

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist bot analytics event")]
    private partial void LogPersistError(Exception ex);
}
