using Asterisk.Platform.Audit;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class RetentionPurgeService : BackgroundService
{
    private readonly ITenantRetentionPolicyStore _policyStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IAuditStore _auditStore;
    private readonly IUsageRecordStore _usageRecordStore;
    private readonly IPurgeLogStore _purgeLogStore;
    private readonly IClock _clock;
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
        IConfiguration configuration)
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

        var hours = configuration.GetValue("Retention:PurgeIntervalHours", 24);
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial run by 5 minutes to let the app start up
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetentionPurgeAsync(stoppingToken);
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
}
