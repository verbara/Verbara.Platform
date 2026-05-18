using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Services;

internal sealed partial class RetentionPurgeService : BackgroundService
{
    /// <summary>
    /// Keyed-service name for the per-cycle <see cref="ResiliencePolicy"/> that wraps each
    /// retention purge pass. Uses DB-heavy budget (long timeout, 1 retry) — circuit-open
    /// skips the current cycle; the next daily tick retries.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.retention-purge";

    private readonly ITenantRetentionPolicyStore _policyStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IAuditStore _auditStore;
    private readonly IUsageRecordStore _usageRecordStore;
    private readonly IPurgeLogStore _purgeLogStore;
    private readonly IClock _clock;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<RetentionPurgeService> _logger;
    private readonly TimeSpan _interval;

    public RetentionPurgeService(
        ITenantRetentionPolicyStore policyStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IAuditStore auditStore,
        IUsageRecordStore usageRecordStore,
        IPurgeLogStore purgeLogStore,
        IClock clock,
        ILogger<RetentionPurgeService> logger,
        IConfiguration configuration,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null)
    {
        _policyStore = policyStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _auditStore = auditStore;
        _usageRecordStore = usageRecordStore;
        _purgeLogStore = purgeLogStore;
        _clock = clock;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;

        var hoursStr = configuration["Retention:PurgeIntervalHours"];
        var hours = int.TryParse(hoursStr, out var h) ? h : 24;
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Delay initial run by 5 minutes to let the app start up
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await RunRetentionPurgeAsync(innerCt);
                            return 0;
                        },
                        stoppingToken);
                }
                catch (CircuitBreakerOpenException)
                {
                    LogCircuitOpen();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogRetentionError(ex);
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(RetentionPurgeService), fatalEx.Message, fatalEx);
            throw;
        }
    }

    internal async Task RunRetentionPurgeAsync(CancellationToken ct)
    {
        var policies = await _policyStore.ListActiveAsync(ct);
        if (policies.Count == 0)
        {
            LogNoActivePolicies();
            return;
        }

        var now = _clock.UtcNow;

        foreach (var policy in policies)
        {
            var entitiesDeleted = new Dictionary<string, int>();
            var tid = new TenantId(policy.TenantId);

            // Conversations + orphaned messages
            if (policy.ConversationRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.ConversationRetentionDays.Value);
                var convDeleted = await _conversationStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (convDeleted > 0)
                    entitiesDeleted["conversations"] = convDeleted;

                var orphanedMsgs = await _messageStore.DeleteOrphanedAsync(tid, ct);
                if (orphanedMsgs > 0)
                    entitiesDeleted["orphanedMessages"] = orphanedMsgs;
            }

            // Auth events
            if (policy.AuthEventRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.AuthEventRetentionDays.Value);
                var deleted = await _authEventStore.DeleteOlderThanAsync(policy.TenantId, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["authEvents"] = deleted;
            }

            // Audit entries
            if (policy.AuditRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.AuditRetentionDays.Value);
                var deleted = await _auditStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["auditEntries"] = deleted;
            }

            // Usage records
            if (policy.UsageRecordRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.UsageRecordRetentionDays.Value);
                var deleted = await _usageRecordStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["usageRecords"] = deleted;
            }

            // Write tombstone if anything was deleted
            if (entitiesDeleted.Count > 0)
            {
                await _purgeLogStore.SaveAsync(new PurgeEntry
                {
                    PurgeId = Guid.NewGuid().ToString("N"),
                    TenantId = policy.TenantId,
                    SubjectType = "retention_policy",
                    SubjectId = policy.TenantId,
                    PerformedBy = "system",
                    Reason = "retention_policy",
                    EntitiesDeleted = entitiesDeleted,
                    PurgedAt = now,
                }, ct);

                var totalDeleted = entitiesDeleted.Values.Sum();
                LogRetentionPurgeCompleted(policy.TenantId, totalDeleted);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention purge completed for tenant {TenantId}: {TotalDeleted} entities deleted")]
    private partial void LogRetentionPurgeCompleted(string tenantId, int totalDeleted);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No active retention policies found -- skipping purge cycle")]
    private partial void LogNoActivePolicies();

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention purge cycle failed")]
    private partial void LogRetentionError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.retention-purge — skipping cycle")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
