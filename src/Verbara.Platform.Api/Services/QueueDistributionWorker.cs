using Verbara.Platform.Api.Health;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Services;

internal sealed partial class QueueDistributionWorker : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-tick <see cref="ResiliencePolicy"/> that wraps each
    /// distribution pass. Circuit-open skips the current tick; the next timer tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.queue-distribution";

    private readonly IConversationStore _conversationStore;
    private readonly ITenantStore _tenantStore;
    private readonly IAgentSelector _agentSelector;
    private readonly IConversationSwitchboard _switchboard;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly DistributionOptions _options;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<QueueDistributionWorker> _logger;

    public QueueDistributionWorker(
        IConversationStore conversationStore,
        ITenantStore tenantStore,
        IAgentSelector agentSelector,
        IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        IClock clock,
        IServiceHeartbeat heartbeat,
        IOptions<DistributionOptions> options,
        ILogger<QueueDistributionWorker> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _conversationStore = conversationStore;
        _tenantStore = tenantStore;
        _agentSelector = agentSelector;
        _switchboard = switchboard;
        _eventBus = eventBus;
        _clock = clock;
        _heartbeat = heartbeat;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PollIntervalMs));

            var pollInterval = TimeSpan.FromMilliseconds(_options.PollIntervalMs);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _heartbeat.RecordTick(nameof(QueueDistributionWorker), pollInterval);
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await DistributeAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    // Circuit open — skip this tick, next timer tick retries.
                    LogCircuitOpen();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogDistributionError(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(QueueDistributionWorker), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task DistributeAsync(CancellationToken ct)
    {
        var tenants = await _tenantStore.GetAllActiveAsync(ct);

        foreach (var tenant in tenants)
        {
            var tid = new TenantId(tenant.TenantId);
            var queued = await _conversationStore.ListQueuedAsync(tid, _options.MaxConversationsPerCycle, ct);

            foreach (var conversation in queued)
            {
                if (conversation.Owner?.OwnerId is null) continue;

                var queueId = conversation.Owner.OwnerId.Value;
                var agentId = await _agentSelector.SelectAgentAsync(
                    tid, queueId, conversation.Channel, preferredAgentId: null, ct);

                if (!agentId.HasValue) continue;

                var result = await _switchboard.OfferToAgentAsync(
                    conversation.ConversationId, tid, agentId.Value, ct);

                if (result.Success)
                {
                    conversation.SetMetadata("_offeredAt", _clock.UtcNow.ToString("O"));
                    conversation.SetMetadata("_offeredTo", agentId.Value.Value);
                    // W5 — remember the originating queue so work-failover can re-queue an orphaned
                    // conversation back to it. The owner is still the Queue at offer time.
                    if (conversation.Owner is { Kind: ConversationOwnerKind.Queue, OwnerId: { } qid })
                        conversation.SetMetadata("originQueueId", qid.Value);
                    await _conversationStore.SaveAsync(conversation, ct);

                    _eventBus.Publish(new ConversationOfferedEvent(
                        tenant.TenantId,
                        conversation.ConversationId.Value,
                        agentId.Value.Value,
                        queueId.Value,
                        conversation.Channel.ToString()));

                    LogOfferSucceeded(conversation.ConversationId.Value, agentId.Value.Value);
                }
                else
                {
                    LogOfferFailed(conversation.ConversationId.Value, result.FailureReason ?? "unknown");
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offered conversation {ConversationId} to agent {AgentId}")]
    private partial void LogOfferSucceeded(string conversationId, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to offer conversation {ConversationId}: {Reason}")]
    private partial void LogOfferFailed(string conversationId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Distribution cycle failed")]
    private partial void LogDistributionError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.queue-distribution — skipping tick")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
