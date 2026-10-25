using Verbara.Platform.Api.Health;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Services;

internal sealed partial class ConversationTimeoutWorker : BackgroundService
{
    /// <summary>
    /// AI-actor identifier stamped on autonomous dispositions and their audit records (ADR-0034).
    /// </summary>
    internal const string AutonomousActorId = "verbara:ai:autonomous-worker";
    /// <summary>
    /// Keyed-service name for the per-tick <see cref="ResiliencePolicy"/> that wraps each
    /// timeout-processing pass. Circuit-open skips the current tick; the next timer tick
    /// retries automatically.
    /// </summary>
    public const string ResiliencePolicyKey = "worker.conversation-timeout";

    private readonly IConversationStore _conversationStore;
    private readonly ITenantStore _tenantStore;
    private readonly IConversationSwitchboard _switchboard;
    private readonly PlatformEventBus _eventBus;
    private readonly IClock _clock;
    private readonly IServiceHeartbeat _heartbeat;
    private readonly DistributionOptions _options;
    private readonly ResiliencePolicy _policy;
    private readonly ILogger<ConversationTimeoutWorker> _logger;

    // ─── Autonomous AI disposition (ADR-0034) — additive deps, all optional so the existing
    //     ctor usage/tests keep compiling. The autonomous branch is reached ONLY when
    //     AutonomousDispositionEnabled is true AND every dependency below is present.
    private readonly IAutonomousDispositionPolicy? _autonomousPolicy;
    private readonly IAiSuggestionStore? _suggestionStore;
    private readonly ITypificationSubmissionStore? _submissionStore;
    private readonly IAuditService? _auditService;
    private readonly AutonomousDispositionMetrics? _autonomousMetrics;
    private readonly ITenantAutonomousDispositionStore? _gateStore;

    public ConversationTimeoutWorker(
        IConversationStore conversationStore,
        ITenantStore tenantStore,
        IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        IClock clock,
        IServiceHeartbeat heartbeat,
        IOptions<DistributionOptions> options,
        ILogger<ConversationTimeoutWorker> logger,
        [FromKeyedServices(ResiliencePolicyKey)] ResiliencePolicy? policy = null,
        IAutonomousDispositionPolicy? autonomousPolicy = null,
        IAiSuggestionStore? suggestionStore = null,
        ITypificationSubmissionStore? submissionStore = null,
        IAuditService? auditService = null,
        AutonomousDispositionMetrics? autonomousMetrics = null,
        ITenantAutonomousDispositionStore? gateStore = null)
    {
        _conversationStore = conversationStore;
        _tenantStore = tenantStore;
        _switchboard = switchboard;
        _eventBus = eventBus;
        _clock = clock;
        _heartbeat = heartbeat;
        _options = options.Value;
        _logger = logger;
        _policy = policy ?? ResiliencePolicy.NoOp;
        _autonomousPolicy = autonomousPolicy;
        _suggestionStore = suggestionStore;
        _submissionStore = submissionStore;
        _auditService = auditService;
        _autonomousMetrics = autonomousMetrics;
        _gateStore = gateStore;
    }

    /// <summary>
    /// True when the global breaker is on AND every autonomous dependency was injected. When false
    /// the wrap-up close path is byte-identical to its pre-ADR-0034 behaviour.
    /// </summary>
    private bool AutonomousEnabled =>
        _options.AutonomousDispositionEnabled
        && _autonomousPolicy is not null
        && _suggestionStore is not null
        && _submissionStore is not null
        && _auditService is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_options.ConversationTimeoutStartupDelayMs), stoppingToken);

            var sweepInterval = TimeSpan.FromMilliseconds(_options.ConversationTimeoutSweepIntervalMs);
            using var timer = new PeriodicTimer(sweepInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _heartbeat.RecordTick(nameof(ConversationTimeoutWorker), sweepInterval);
                try
                {
                    await _policy.ExecuteAsync(
                        ResiliencePolicyKey,
                        async innerCt =>
                        {
                            await ProcessTimeoutsAsync(innerCt);
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
                    LogTimeoutCycleError(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — host is stopping. Don't rethrow.
        }
        catch (Exception fatalEx)
        {
            LogWorkerCrash(nameof(ConversationTimeoutWorker), fatalEx.Message, fatalEx);
            throw;
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

            if ((now - offeredAt.ToUtcInstant()).TotalSeconds <= _options.OfferTimeoutSeconds)
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

        // Fast path: global breaker OFF (or deps missing) — byte-identical to pre-ADR-0034. No
        // policy lookup, no extra allocation, no audit. This is the production default.
        if (!AutonomousEnabled)
        {
            foreach (var conv in wrapUp)
            {
                if (!IsWrapUpExpired(conv, now))
                    continue;

                await CloseWrapUpBlankAsync(conv, tenantId, now, ct);
            }
            return;
        }

        // Autonomous branch: breaker ON. Per-tenant per-cycle cap on stamps (ADR-0034 Decision 6).
        var stampedThisCycle = 0;
        var cap = _options.AutonomousDispositionPerCycleCap;

        foreach (var conv in wrapUp)
        {
            if (!IsWrapUpExpired(conv, now))
                continue;

            var capReached = stampedThisCycle >= cap;
            if (await ProcessAutonomousWrapUpAsync(conv, tid, tenantId, now, capReached, ct))
                stampedThisCycle++;
        }
    }

    /// <summary>
    /// Has the wrap-up been idle beyond <see cref="DistributionOptions.DefaultWrapUpTimeoutSeconds"/>?
    /// Extracted verbatim from the original loop so the gate-OFF path stays behaviourally identical.
    /// </summary>
    private bool IsWrapUpExpired(Conversation conv, DateTimeOffset now)
    {
        var reference = conv.UpdatedAt ?? conv.CreatedAt;
        return (now - reference).TotalSeconds > _options.DefaultWrapUpTimeoutSeconds;
    }

    /// <summary>
    /// The pre-ADR-0034 blank close: transition to <see cref="ConversationState.Closed"/>, save, and
    /// publish <see cref="ConversationStateChangedEvent"/>. Used by both the gate-OFF fast path and
    /// the autonomous Skip path so the two are allocation/behaviour-identical.
    /// </summary>
    private async Task CloseWrapUpBlankAsync(Conversation conv, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
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

    /// <summary>
    /// Autonomous-branch handling of a single expired wrap-up (ADR-0034 Decisions 2/3/6). Returns
    /// <see langword="true"/> only when it stamped an AutoAi disposition (so the caller advances the
    /// per-cycle cap counter). Flow:
    /// <list type="bullet">
    ///   <item><description><b>GateDisabled</b> (tenant not opted in) → byte-identical blank close, NO audit/metric.</description></item>
    ///   <item><description><b>Other Skip reason</b> → blank close + one <c>AutonomousSkipped</c> audit + skip metric.</description></item>
    ///   <item><description><b>Commit, cap reached</b> → leave in WrapUp for the next cycle (do NOT close).</description></item>
    ///   <item><description><b>Commit, under cap</b> → state-based CAS close; if it won (1 row) stamp the AutoAi submission + <c>AutonomousCommit</c> audit + commit metric; if it lost (0 rows, a human typified concurrently) skip silently.</description></item>
    /// </list>
    /// </summary>
    private async Task<bool> ProcessAutonomousWrapUpAsync(
        Conversation conv, TenantId tid, string tenantId, DateTimeOffset now, bool capReached, CancellationToken ct)
    {
        // Gate-first short-circuit (efficiency during gradual rollout): the activation gate is the
        // cheapest gate and the common miss while most tenants are NOT opted in. Checking it here —
        // BEFORE the suggestion-store read + the full policy evaluation — avoids N suggestion reads
        // per non-opted-in tenant per cycle. Behaviour is unchanged: a non-opted-in tenant still
        // closes blank with NO audit/metric (byte-identical to the GateDisabled fall-through below,
        // which is byte-identical to the gate-OFF fast path). The policy keeps its own gate check
        // (defence in depth) for when this store isn't injected.
        if (_gateStore is not null)
        {
            var gate = await _gateStore.GetAsync(conv.TenantId, ct);
            if (gate is not { IsActive: true })
            {
                await CloseWrapUpBlankAsync(conv, tenantId, now, ct);
                return false;
            }
        }

        // #476 (dialer-honours-dnc-callbacks-and-agent-dispositions D17): a typified Conversation keeps its
        // submission. This read is only the common case's shortcut (it saves the suggestion read and the policy); the
        // guarantee is the stamp's insert-or-nothing below. The close is the same conditional close the stamp uses,
        // so when several replicas sweep one row, only the one whose close won announces, audits and counts it
        // (round-6 audit F2).
        if (await _submissionStore!.GetByConversationIdAsync(conv.TenantId, conv.ConversationId, ct) is not null)
        {
            if (await TryCloseWrapUpConditionallyAsync(conv, tid, tenantId, now, ct))
                await RecordAlreadyTypifiedAsync(conv, tenantId, now, ct);
            return false;
        }

        var tenantEntityId = EntityId.From(conv.TenantId.Value);
        var suggestion = await _suggestionStore!.GetLatestForConversationAsync(
            tenantEntityId, conv.ConversationId, ct);

        var decision = await _autonomousPolicy!.EvaluateAsync(conv, suggestion, ct);

        if (!decision.ShouldCommit)
        {
            var reason = decision.Reason ?? AutonomousSkipReason.NoSuggestion;

            // GateDisabled = the tenant has no active gate ⇒ byte-identical to today (close blank,
            // no audit, no metric). Decision 1: a non-opted-in tenant's close path is unchanged.
            if (reason == AutonomousSkipReason.GateDisabled)
            {
                await CloseWrapUpBlankAsync(conv, tenantId, now, ct);
                return false;
            }

            // Opted-in tenant, but a gate failed (license / suggestion / threshold / schema / state):
            // close blank (today's behaviour) AND emit a single AutonomousSkipped audit + metric.
            await CloseWrapUpBlankAsync(conv, tenantId, now, ct);
            await EmitSkippedAuditAsync(conv, now, reason, ct);
            _autonomousMetrics!.RecordSkip(tenantId, reason);
            LogAutonomousSkipped(conv.ConversationId.Value, reason);
            return false;
        }

        // Commit decision but the per-cycle cap is already hit: defer this candidate to the next
        // cycle. Decision 4: do NOT blank-close it while autonomous is ON and it qualifies.
        if (capReached)
        {
            LogAutonomousCapDeferred(conv.ConversationId.Value);
            return false;
        }

        // Defence in depth: the policy's contract guarantees a commit decision implies a non-null
        // suggestion (the NoSuggestion gate precedes commit), but a mis-implemented policy could
        // violate it. Guard the dereference: if commit somehow arrived with a null suggestion, skip
        // + log rather than throw (a worker crash would abort the whole tenant sweep).
        if (suggestion is null)
        {
            LogAutonomousCommitNullSuggestion(conv.ConversationId.Value);
            return false;
        }

        // State-based CAS: close ONLY if still WrapUp. A concurrent human typify wins (0 rows).
        if (!await TryCloseWrapUpConditionallyAsync(conv, tid, tenantId, now, ct))
        {
            // A human already typified/closed it — skip silently (do NOT stamp, do NOT overwrite).
            LogAutonomousRaceLost(conv.ConversationId.Value);
            return false;
        }

        if (!await StampAutonomousDispositionAsync(conv, suggestion, decision, now, ct))
        {
            // D17: a typification committed between the read above and the stamp — it is kept, the stamp stored
            // nothing, and the per-cycle cap does not count it.
            await RecordAlreadyTypifiedAsync(conv, tenantId, now, ct);
            return false;
        }

        _autonomousMetrics!.RecordCommit(tenantId);
        LogAutonomousCommit(conv.ConversationId.Value, decision.Confidence);
        return true;
    }

    /// <summary>
    /// The state-based conditional close (CAS on <c>state = WrapUp</c>). When it wins, the authoritative WrapUp→Closed
    /// transition already happened atomically in the store's conditional UPDATE, so State/ClosedAt are mirrored on the
    /// in-memory entity directly — never re-routed through <c>conv.TransitionTo</c> (that would re-run guard logic over
    /// a close the database already owns) — and the same state-change event the blank path emits is published.
    /// </summary>
    /// <returns><see langword="false"/> when another writer (a human, another replica's sweep) closed it first.</returns>
    private async Task<bool> TryCloseWrapUpConditionallyAsync(
        Conversation conv, TenantId tid, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await _conversationStore.TryConditionalCloseWrapUpAsync(tid, conv.ConversationId, now, ct))
            return false;

        conv.State = ConversationState.Closed;
        conv.ClosedAt ??= now;
        conv.UpdatedAt = now;

        _eventBus.Publish(new ConversationStateChangedEvent(
            tenantId,
            conv.ConversationId.Value,
            nameof(ConversationState.WrapUp),
            nameof(ConversationState.Closed)));
        return true;
    }

    /// <summary>
    /// D17: the timeout kept a stored submission and only closed the Conversation — one <c>AutonomousSkipped</c> audit
    /// with reason <see cref="AutonomousSkipReason.AlreadyTypified"/>, the skip metric and a log line; no commit audit
    /// and no commit metric.
    /// </summary>
    private async Task RecordAlreadyTypifiedAsync(Conversation conv, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        await EmitSkippedAuditAsync(conv, now, AutonomousSkipReason.AlreadyTypified, ct);
        _autonomousMetrics!.RecordSkip(tenantId, AutonomousSkipReason.AlreadyTypified);
        LogAutonomousSkipped(conv.ConversationId.Value, AutonomousSkipReason.AlreadyTypified);
    }

    /// <summary>
    /// Persists the AutoAi <see cref="TypificationSubmission"/> and emits the <c>AutonomousCommit</c>
    /// AI-actor audit (with the correction-window retention floor). Called only after the CAS close won.
    /// The write is an insert-or-nothing (D17, #476): a submission already stored — the agent's — is kept.
    /// </summary>
    /// <returns><see langword="false"/>, having written and audited nothing, when a submission was already stored.</returns>
    private async Task<bool> StampAutonomousDispositionAsync(
        Conversation conv, AiSuggestionRecord suggestion, AutonomousDispositionDecision decision,
        DateTimeOffset now, CancellationToken ct)
    {
        var leaf = decision.LeafNodeId ?? suggestion.SuggestedLeafNodeId;
        var nodePath = decision.NodePath ?? suggestion.SuggestedNodePath;

        var submission = new TypificationSubmission
        {
            TenantId = conv.TenantId,
            ConversationId = conv.ConversationId,
            AgentId = EntityId.From(AutonomousActorId),
            SchemaId = suggestion.SchemaId,
            SchemaVersion = suggestion.SchemaVersion,
            SelectedNodePath = nodePath.Select(EntityId.From).ToList(),
            LeafNodeId = leaf,
            FieldValues = suggestion.SuggestedFieldValues,
            AiSuggested = true,
            AiConfidence = decision.Confidence,
            AiAccepted = true,
            Source = SubmissionSource.AutoAi,
            SuggestedLeafNodeId = suggestion.SuggestedLeafNodeId,
            SuggestedNodePath = suggestion.SuggestedNodePath,
            CompletedAt = now,
            AutonomousActorId = AutonomousActorId,
        };

        if (!await _submissionStore!.TryInsertAsync(submission, dispatch: null, ct))
            return false;

        var retainUntil = now.AddDays(_options.AutonomousCorrectionWindowDays);
        var nodePathStr = string.Join(" > ", nodePath);
        await _auditService!.RecordAsync(
            conv.TenantId,
            category: "conversations",
            action: "typification.autonomous.commit",
            severity: "info",
            actorId: AutonomousActorId,
            actorType: "ai",
            targetId: conv.ConversationId.Value,
            targetType: "Conversation",
            metadata: new Dictionary<string, string>
            {
                ["node_path"] = nodePathStr,
                ["leaf_node_id"] = leaf.Value,
                ["confidence"] = decision.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                ["tenant"] = conv.TenantId.Value,
                ["conversation"] = conv.ConversationId.Value,
            },
            retainUntil: retainUntil,
            ct: ct);
        return true;
    }

    /// <summary>
    /// Emits a single <c>AutonomousSkipped</c> AI-actor audit carrying the skip reason (ADR-0034
    /// Decision 3). No retention floor: a skip records no disposition fact to preserve.
    /// </summary>
    private Task EmitSkippedAuditAsync(
        Conversation conv, DateTimeOffset now, AutonomousSkipReason reason, CancellationToken ct) =>
        _auditService!.RecordAsync(
            conv.TenantId,
            category: "conversations",
            action: "typification.autonomous.skipped",
            severity: "info",
            actorId: AutonomousActorId,
            actorType: "ai",
            targetId: conv.ConversationId.Value,
            targetType: "Conversation",
            metadata: new Dictionary<string, string>
            {
                ["reason"] = reason.ToString(),
                ["tenant"] = conv.TenantId.Value,
                ["conversation"] = conv.ConversationId.Value,
            },
            ct: ct);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Offer expired for conversation {ConversationId}, agent {AgentId}")]
    private partial void LogOfferExpired(string conversationId, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queue timeout — conversation {ConversationId} abandoned")]
    private partial void LogQueueAbandoned(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WrapUp timeout — conversation {ConversationId} auto-closed")]
    private partial void LogWrapUpClosed(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Autonomous disposition stamped on conversation {ConversationId} (confidence {Confidence:F4})")]
    private partial void LogAutonomousCommit(string conversationId, double confidence);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Autonomous disposition skipped for conversation {ConversationId} (reason {Reason})")]
    private partial void LogAutonomousSkipped(string conversationId, AutonomousSkipReason reason);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Autonomous disposition deferred for conversation {ConversationId} — per-cycle cap reached")]
    private partial void LogAutonomousCapDeferred(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Autonomous disposition CAS lost for conversation {ConversationId} — a human typified concurrently")]
    private partial void LogAutonomousRaceLost(string conversationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Autonomous commit decision for conversation {ConversationId} had a null suggestion — skipping (policy invariant violated)")]
    private partial void LogAutonomousCommitNullSuggestion(string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conversation timeout cycle failed")]
    private partial void LogTimeoutCycleError(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Circuit open for worker.conversation-timeout — skipping tick")]
    private partial void LogCircuitOpen();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "[WORKER] {WorkerName} crashed fatally — host will shut down for restart. Reason: {Reason}")]
    private partial void LogWorkerCrash(string workerName, string reason, Exception ex);
}
