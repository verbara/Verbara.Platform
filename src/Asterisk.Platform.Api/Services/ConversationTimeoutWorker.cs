using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Switchboard;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class ConversationTimeoutWorker : BackgroundService
{
    private readonly IConversationStore _conversationStore;
    private readonly ITenantStore _tenantStore;
    private readonly IConversationSwitchboard _switchboard;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly DistributionOptions _options;
    private readonly ILogger<ConversationTimeoutWorker> _logger;

    public ConversationTimeoutWorker(
        IConversationStore conversationStore,
        ITenantStore tenantStore,
        IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        IClock clock,
        IOptions<DistributionOptions> options,
        ILogger<ConversationTimeoutWorker> logger)
    {
        _conversationStore = conversationStore;
        _tenantStore = tenantStore;
        _switchboard = switchboard;
        _eventBus = eventBus;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessTimeoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogTimeoutCycleError(ex);
            }
        }
    }

    internal async Task ProcessTimeoutsAsync(CancellationToken ct)
    {
        var tenants = await _tenantStore.GetAllActiveAsync(ct);
        var now = _clock.UtcNow;

        foreach (var tenant in tenants)
        {
            var tid = new TenantId(tenant.TenantId);

            await ProcessOfferTimeoutsAsync(tid, tenant.TenantId, now, ct);
            await ProcessQueueTimeoutsAsync(tid, tenant.TenantId, now, ct);
            await ProcessWrapUpTimeoutsAsync(tid, tenant.TenantId, now, ct);
        }
    }

    private async Task ProcessOfferTimeoutsAsync(
        TenantId tid, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var offered = await _conversationStore.ListByStateAsync(tid, ConversationState.Offered, 100, ct);

        foreach (var conv in offered)
        {
            if (!conv.Metadata.TryGetValue("_offeredAt", out var offeredAtStr))
                continue;

            if (!DateTimeOffset.TryParse(offeredAtStr, out var offeredAt))
                continue;

            if ((now - offeredAt).TotalSeconds <= _options.OfferTimeoutSeconds)
                continue;

            var agentId = conv.Metadata.TryGetValue("_offeredTo", out var agentIdStr)
                ? agentIdStr
                : "unknown";

            await _switchboard.RejectAsync(conv.ConversationId, tid, EntityId.From(agentId), ct);

            _eventBus.Publish(new ConversationOfferExpiredEvent(
                tenantId, conv.ConversationId.Value, agentId));

            LogOfferExpired(conv.ConversationId.Value, agentId);
        }
    }

    private async Task ProcessQueueTimeoutsAsync(
        TenantId tid, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var queued = await _conversationStore.ListByStateAsync(tid, ConversationState.Queued, 100, ct);

        foreach (var conv in queued)
        {
            if ((now - conv.CreatedAt).TotalSeconds <= _options.DefaultQueueTimeoutSeconds)
                continue;

            conv.TransitionTo(ConversationState.Abandoned, now);
            conv.UpdatedAt = now;
            await _conversationStore.SaveAsync(conv, ct);

            var queueId = conv.Owner?.OwnerId?.Value ?? "unknown";

            _eventBus.Publish(new ConversationAbandonedEvent(
                tenantId, conv.ConversationId.Value, queueId));

            LogQueueAbandoned(conv.ConversationId.Value);
        }
    }

    private async Task ProcessWrapUpTimeoutsAsync(
        TenantId tid, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var wrapUp = await _conversationStore.ListByStateAsync(tid, ConversationState.WrapUp, 100, ct);

        foreach (var conv in wrapUp)
        {
            var reference = conv.UpdatedAt ?? conv.CreatedAt;

            if ((now - reference).TotalSeconds <= _options.DefaultWrapUpTimeoutSeconds)
                continue;

            conv.TransitionTo(ConversationState.Closed, now);
            conv.UpdatedAt = now;
            await _conversationStore.SaveAsync(conv, ct);

            _eventBus.Publish(new ConversationStateChangedEvent(
                tenantId,
                conv.ConversationId.Value,
                nameof(ConversationState.WrapUp),
                nameof(ConversationState.Closed)));

            LogWrapUpClosed(conv.ConversationId.Value);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offer expired for conversation {ConversationId}, agent {AgentId}")]
    private partial void LogOfferExpired(string conversationId, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queue timeout — conversation {ConversationId} abandoned")]
    private partial void LogQueueAbandoned(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WrapUp timeout — conversation {ConversationId} auto-closed")]
    private partial void LogWrapUpClosed(string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation timeout cycle failed")]
    private partial void LogTimeoutCycleError(Exception ex);
}
