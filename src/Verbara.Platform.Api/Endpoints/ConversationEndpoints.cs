using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;
using Verbara.Platform.Typification.Validation;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Dispositions;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Endpoints;

internal static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/conversations").RequireAuthorization("Authenticated").RequireOperationalTenant();

        group.MapGet("/", ListConversations);
        group.MapGet("/{id}", GetConversation);
        group.MapGet("/{id}/messages", GetMessages);
        group.MapPost("/{id}/messages", SendMessage);
        group.MapPost("/{id}/accept", AcceptConversation);
        group.MapPost("/{id}/reject", RejectConversation);
        // Every Agent, Supervisor and Manager holds contacts:conversation:transfer; the handler then
        // requires the owner or a supervisor, and a target that exists.
        group.MapPost("/{id}/transfer", TransferConversation)
            .RequireAuthorization("Permission:contacts:conversation:transfer");
        // close-chat-without-typification-licence D9: a refused close answers 409 with a stable code
        // (typification-required, conversation-not-closable), written as a JSON result.
        group.MapPost("/{id}/close", CloseConversation)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/typification-form", GetTypificationForm);
        group.MapPost("/{id}/typify", TypifyConversation)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);
        // D1 — AI auto-disposition suggestion. The conversations group is NOT
        // license-gated (agents must always be able to close work), so the gate is
        // applied to THIS endpoint only, requiring BOTH AdvancedTypification AND
        // TypificationAi: the LicenseGateMiddleware reads one LicenseFeatureMetadata and
        // checks HasFlag, so a combined flags value gates on EVERY set bit (402 unless both).
        // E3 — also apply the "llm" policy (the only RequireRateLimiting policy on any route — WebChat and webhooks
        // use the endpoint-filter AnonymousRateLimitPolicy; NOT tier-bypassed): LLM calls are expensive at every tier.
        group.MapPost("/{id}/typification-suggestion", GetTypificationSuggestion)
            .RequireLicenseFeature(LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi)
            .RequireRateLimiting("llm");
        group.MapPost("/{id}/hold", HoldConversation);
        group.MapPost("/{id}/unhold", UnholdConversation);
        // agent-started-conversations (D14): the 403 (not-an-agent), 500 (start-persist-failed) and 502
        // (channel-send-failed) bodies are written as JSON results, which declare no response metadata of their own.
        group.MapPost("/", CreateConversation)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<ConversationStartRefusal>(StatusCodes.Status500InternalServerError)
            .Produces<ConversationStartRefusal>(StatusCodes.Status502BadGateway);
        // ADR-0034 — supervisor correction of an autonomously stamped disposition. Gated by the
        // dedicated `typification:correct-autonomous` permission (NOT AdminOnly — a supervisor is
        // the realistic actor). The `Permission:` prefix forces the PermissionPolicyProvider to
        // build a PermissionRequirement even though the permission id carries only one colon.
        group.MapPost("/{id}/typification-correction", CorrectTypification)
            .RequireAuthorization("Permission:typification:correct-autonomous");
    }

    private static async Task<Ok<PagedResult<Conversation>>> ListConversations(
        HttpContext context,
        [FromServices] IConversationStore store,
        ConversationState? state,
        string? queueId,
        string? agentId,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var query = new ConversationQuery
        {
            State = state,
            AssignedAgentId = agentId is not null ? EntityId.From(agentId) : null,
            Page = page,
            PageSize = pageSize,
        };

        var result = await store.ListAsync(tenantId, query, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<Conversation>, NotFound>> GetConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return conversation is null ? TypedResults.NotFound() : TypedResults.Ok(conversation);
    }

    private static async Task<Ok<IReadOnlyList<Message>>> GetMessages(
        string id,
        HttpContext context,
        [FromServices] IMessageStore store,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var messages = await store.GetConversationMessagesAsync(tenantId, EntityId.From(id), limit, offset, ct);
        return TypedResults.Ok(messages);
    }

    // Only the owner sends on a conversation, as its Agent profile.
    private static async Task<Results<Ok<Message>, NotFound, JsonHttpResult<ErrorResponse>>> SendMessage(
        string id,
        HttpContext context,
        [FromBody] SendMessageRequest body,
        IConversationService conversationService,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        if (!actor.Owns(conversation))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);

        var envelope = new MessageEnvelope(
        [
            new TextBlock(body.Text),
        ]);

        // A channel rule refusal (WhatsApp outside its 24-hour window: whatsapp-template-required) is a 409 with
        // the machine code; nothing was stored or sent.
        var outcome = await conversationService.TrySendMessageAsync(
            conversation.ConversationId,
            tenantId,
            envelope,
            actor.Agent.AgentId,
            ConversationOwnerKind.Agent,
            templateId: null,
            ct);

        return outcome.Message is { } message
            ? TypedResults.Ok(message)
            : ConversationActor.Conflict(outcome.RefusalCode!);
    }

    // An offer is accepted only by the agent it was made to, which then owns the conversation.
    private static async Task<Results<Ok<OwnershipResult>, NotFound, JsonHttpResult<ErrorResponse>>> AcceptConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        if (!actor.IsOfferedTo(conversation))
            return ConversationActor.Forbidden(ConversationActor.NotOfferedToYou);

        var result = await switchboard.AcceptAsync(conversation.ConversationId, tenantId, actor.Agent.AgentId, ct);
        return result.Success
            ? TypedResults.Ok(result)
            : ConversationActor.Conflict(result.FailureReason ?? "The conversation could not be accepted.");
    }

    // An offer is rejected only by the agent it was made to; the conversation returns to its queue.
    private static async Task<Results<Ok<OwnershipResult>, NotFound, JsonHttpResult<ErrorResponse>>> RejectConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        if (!actor.IsOfferedTo(conversation))
            return ConversationActor.Forbidden(ConversationActor.NotOfferedToYou);

        var result = await switchboard.RejectAsync(conversation.ConversationId, tenantId, actor.Agent.AgentId, ct);
        return result.Success
            ? TypedResults.Ok(result)
            : ConversationActor.Conflict(result.FailureReason ?? "The offer could not be rejected.");
    }

    // The route requires contacts:conversation:transfer. The owner, or a supervisor acting on a
    // conversation it does not own, moves it to an agent or a queue of the tenant; every transfer is
    // audited, a supervisor's with by_supervisor.
    private static async Task<Results<Ok<OwnershipResult>, NotFound, BadRequest<ErrorResponse>, JsonHttpResult<ErrorResponse>>> TransferConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        [FromServices] IAgentAccountStatusLookup accountStatus,
        [FromServices] IQueueStore queues,
        [FromServices] IAuditService audit,
        [FromBody] TransferRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (body.TargetQueueId is null && body.TargetAgentId is null)
            return TypedResults.BadRequest(new ErrorResponse("Either targetQueueId or targetAgentId must be specified"));

        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        var bySupervisor = !actor.Owns(conversation);
        if (bySupervisor && !await ConversationActor.IsSupervisorAsync(context))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        ConversationActor.AddOwner(metadata, "from_owner", conversation.Owner);

        OwnershipResult result;
        if (body.TargetQueueId is not null)
        {
            var queueId = EntityId.From(body.TargetQueueId);
            if (await queues.GetByIdAsync(tenantId, queueId, ct) is null)
                return TypedResults.BadRequest(new ErrorResponse(ConversationActor.TargetQueueNotFound));

            result = await switchboard.TransferToQueueAsync(conversation.ConversationId, tenantId, queueId, ct);
            metadata["target_queue"] = queueId.Value;
        }
        else
        {
            var agentId = EntityId.From(body.TargetAgentId!);
            if (!await ConversationActor.IsOwnableTargetAsync(agents, accountStatus, tenantId, agentId, ct))
                return TypedResults.BadRequest(new ErrorResponse(ConversationActor.TargetAgentNotFound));

            result = await switchboard.TransferToAgentAsync(
                conversation.ConversationId, tenantId, agentId,
                new OwnershipChange(OwnershipChangeKind.Transferred, CallerIdentity.ResolveUserIdOrSystem(context.User)), ct);
            metadata["target_agent"] = agentId.Value;
        }

        if (!result.Success)
            return ConversationActor.Conflict(result.FailureReason ?? "The conversation could not be transferred.");

        if (bySupervisor)
            metadata[ConversationAudit.BySupervisor] = CallerIdentity.ResolveUserIdOrSystem(context.User);
        await ConversationAudit.TryRecordAsync(
            context, audit, tenantId, "conversation.transferred", conversation.ConversationId, metadata, ct);

        return TypedResults.Ok(result);
    }

    // The owner, or a supervisor (audited with by_supervisor), closes a conversation
    // (close-chat-without-typification-licence, design D10). The decision is taken from the state and
    // channel read here, before anything is mutated — never by catching a refused TransitionTo:
    //   - WrapUp / WaitingForCustomer / Resolved: the existing edge to Closed, now publishing the change
    //     (from WrapUp as a compare-and-set, D4).
    //   - Active / OnHold / Consulting, non-voice, agent-held: a bound published schema means typification
    //     is demanded (409 typification-required, nothing mutated, D5); otherwise the chat wraps up through
    //     the switchboard (the one place the owner's capacity unit is released, once per won wrap-up — D2/D3) and
    //     the WrapUp → Closed step runs as a compare-and-set that cannot clobber a concurrent close (D4).
    //   - Anything else (Queued, Offered, Snoozed, Escalated, terminal, live voice): 409 conversation-not-closable.
    private static async Task<Results<Ok<Conversation>, NotFound, JsonHttpResult<ErrorResponse>>> CloseConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore store,
        [FromServices] IAgentStore agents,
        [FromServices] IAuditService audit,
        [FromServices] ITypificationResolver resolver,
        [FromServices] IConversationSwitchboard switchboard,
        PlatformEventBus eventBus,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversationId = EntityId.From(id);
        var conversation = await store.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        var bySupervisor = !actor.Owns(conversation);
        if (bySupervisor && !await ConversationActor.IsSupervisorAsync(context))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);

        var owner = conversation.Owner;
        var fromState = conversation.State;

        if (ConversationStateMachine.IsFailoverWork(fromState))
        {
            // Agent-held work. Voice wraps up through the bridge, which owns that edge and its release (D8).
            if (conversation.Channel == ChannelType.Voice
                || owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerAgentId })
                return ConversationActor.Conflict(ConversationActor.NotClosable);

            if (await resolver.ResolveForConversationAsync(conversation, ct) is not null)
                return ConversationActor.Conflict(ConversationActor.TypificationRequired);

            // The wrap-up is predicated on the owner the actor check above was decided against: a transfer
            // that committed since makes it fail (409) rather than close the new owner's chat (D3).
            var wrapUp = await switchboard.WrapUpAsync(tenantId, conversationId, ownerAgentId, ct);
            if (!wrapUp.Success)
                return await AlreadyClosedOrNotClosableAsync(store, tenantId, conversationId, ct);

            var now = clock.UtcNow;
            if (!await store.TryConditionalCloseWrapUpAsync(tenantId, conversationId, now, ct))
            {
                // The wrap-up was ours but a concurrent close (worker, supervisor, a second request) moved the
                // row out of WrapUp first. Our wrap-up still released the unit; report what the row is now (D4),
                // auditing the supervisor's part only when the conversation did end up Closed.
                var outcome = await AlreadyClosedOrNotClosableAsync(store, tenantId, conversationId, ct);
                if (outcome.Result is Ok<Conversation>)
                    await RecordCloseAuditAsync(context, audit, tenantId, conversationId, owner, bySupervisor, skippedTypification: true, ct);
                return outcome;
            }

            eventBus.Publish(new ConversationStateChangedEvent(
                tenantId.Value, conversationId.Value, nameof(ConversationState.WrapUp), nameof(ConversationState.Closed)));
            await RecordCloseAuditAsync(context, audit, tenantId, conversationId, owner, bySupervisor, skippedTypification: true, ct);

            return TypedResults.Ok(await ReloadClosedAsync(store, tenantId, conversationId, conversation, now, ct));
        }

        if (!ConversationStateMachine.CanTransition(fromState, ConversationState.Closed))
            return ConversationActor.Conflict(ConversationActor.NotClosable);

        if (fromState == ConversationState.WrapUp)
        {
            // WrapUp → Closed as a compare-and-set (D4): a concurrent close of the same chat (a racing /close
            // whose wrap-up won, the timeout worker, a supervisor) may get there first. Re-running TransitionTo
            // on a row another request just closed would throw (the in-memory store shares its entity), and a
            // full-row save would clobber that close on Postgres.
            var closedAt = clock.UtcNow;
            if (!await store.TryConditionalCloseWrapUpAsync(tenantId, conversationId, closedAt, ct))
                return await AlreadyClosedOrNotClosableAsync(store, tenantId, conversationId, ct);

            eventBus.Publish(new ConversationStateChangedEvent(
                tenantId.Value, conversationId.Value, nameof(ConversationState.WrapUp), nameof(ConversationState.Closed)));
            await RecordCloseAuditAsync(context, audit, tenantId, conversationId, owner, bySupervisor, skippedTypification: false, ct);
            return TypedResults.Ok(await ReloadClosedAsync(store, tenantId, conversationId, conversation, closedAt, ct));
        }

        // WaitingForCustomer / Resolved. Re-check the live state just before the mutation: the in-memory store
        // hands out its stored entity, so the state read above can be stale by now.
        if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.Closed))
            return await AlreadyClosedOrNotClosableAsync(store, tenantId, conversationId, ct);

        conversation.TransitionTo(ConversationState.Closed);
        await store.SaveAsync(conversation, ct);

        eventBus.Publish(new ConversationStateChangedEvent(
            tenantId.Value, conversationId.Value, fromState.ToString(), nameof(ConversationState.Closed)));
        await RecordCloseAuditAsync(context, audit, tenantId, conversationId, owner, bySupervisor, skippedTypification: false, ct);

        return TypedResults.Ok(conversation);
    }

    // The store committed → Closed atomically; return the row as stored rather than re-running TransitionTo on
    // the instance loaded before the conditional update.
    private static async Task<Conversation> ReloadClosedAsync(
        IConversationStore store, TenantId tenantId, EntityId conversationId, Conversation loaded, DateTimeOffset now, CancellationToken ct)
    {
        var closed = await store.GetByIdAsync(tenantId, conversationId, ct) ?? loaded;
        if (closed.State != ConversationState.Closed)
        {
            closed.State = ConversationState.Closed;
            closed.ClosedAt ??= now;
            closed.UpdatedAt = now;
        }

        return closed;
    }

    // A close that lost a race: 200 with the row when someone else already closed it (D4), else 409.
    private static async Task<Results<Ok<Conversation>, NotFound, JsonHttpResult<ErrorResponse>>> AlreadyClosedOrNotClosableAsync(
        IConversationStore store, TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var current = await store.GetByIdAsync(tenantId, conversationId, ct);
        return current is { State: ConversationState.Closed }
            ? TypedResults.Ok(current)
            : ConversationActor.Conflict(ConversationActor.NotClosable);
    }

    // A supervisor's close of a conversation it does not own is audited with by_supervisor and the owner
    // before the close; a close that wrapped up without a schema also records typification=skipped-no-schema.
    private static async Task RecordCloseAuditAsync(
        HttpContext context,
        IAuditService audit,
        TenantId tenantId,
        EntityId conversationId,
        ConversationOwner? owner,
        bool bySupervisor,
        bool skippedTypification,
        CancellationToken ct)
    {
        if (!bySupervisor)
            return;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConversationAudit.BySupervisor] = CallerIdentity.ResolveUserIdOrSystem(context.User),
        };
        ConversationActor.AddOwner(metadata, "owner", owner);
        if (skippedTypification)
            metadata["typification"] = "skipped-no-schema";
        await ConversationAudit.TryRecordAsync(context, audit, tenantId, "conversation.closed", conversationId, metadata, ct);
    }

    private static async Task<Results<Ok<TypificationFormResponse>, NotFound>> GetTypificationForm(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] ITypificationResolver resolver,
        [FromServices] ITypificationPrefillResolver prefillResolver,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversationStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return TypedResults.NotFound();

        // C9 — resolve the wrap-up prefill (preselected reason path + prefilled field
        // values) from the conversation's captured context. Pure + never throws; empty
        // collections map to null so the client distinguishes "no prefill" cleanly.
        var prefill = prefillResolver.ResolvePrefill(resolved.Schema, resolved.SubtreeRoot, conversation);

        return TypedResults.Ok(new TypificationFormResponse(
            Schema: TypificationEndpoints.ToSchemaDto(resolved.Schema),
            SubtreeRootNodeId: resolved.SubtreeRoot?.Value,
            PrefilledNodePath: prefill.PrefilledNodePath.Count > 0
                ? prefill.PrefilledNodePath.Select(n => n.Value).ToArray()
                : null,
            PrefilledFieldValues: prefill.PrefilledFieldValues.Count > 0
                ? prefill.PrefilledFieldValues
                : null));
    }

    // D1 — AI auto-disposition suggestion (P2a/C1). Best-effort: every
    // "can't suggest" path returns 200 with an all-null TypificationSuggestionResponse
    // (no schema / AI disabled / Mode==Off / classifier degraded / below confidence /
    // sentiment-gated) so the client treats it uniformly as "no suggestion, fall back
    // to manual". The route is license-gated (AdvancedTypification + TypificationAi).
    // B2: every non-null classification is persisted to IAiSuggestionStore BEFORE gating
    // (so later reconciliation can cover even shadow-suppressed suggestions). When
    // Mode == Shadow the record is saved but the response surfaces nothing.
    // C1: the Band field on the response is server-authoritative; client cannot escalate.
    private static async Task<Results<Ok<TypificationSuggestionResponse>, NotFound, JsonHttpResult<ErrorResponse>>> GetTypificationSuggestion(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] ITypificationResolver resolver,
        [FromServices] ITypificationAiClassifier classifier,
        [FromServices] IMessageStore messageStore,
        [FromServices] IAiSuggestionStore aiSuggestions,
        [FromServices] IClock clock,
        [FromServices] TypificationAiMetrics aiMetrics,
        [FromServices] ITypificationCalibration calibration,
        [FromServices] ITypificationTokenBudget budget,
        [FromServices] IAuditService auditService,
        [FromServices] IQuotaEnforcementService quota,
        [FromServices] ITypificationCreditMeter creditMeter,
        [FromServices] ITenantLlmConfigStore llmConfigStore,
        [FromServices] IFeatureGateService featureGate,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversationId = EntityId.From(id);

        // P2c.2 (C3) — read the tenant's LLM source ONCE. Quota gating + credit metering apply
        // ONLY when the tenant opted into the platform-managed LLM (Verbara operator key); BYO
        // (or no config) is never metered or quota-gated here — the tenant pays its own provider.
        var llmConfig = await llmConfigStore.GetAsync(EntityId.From(tenantId.Value), ct);
        var isPlatformManaged = llmConfig is { AiSource: AiSource.PlatformManaged };

        // 1. Conversation must exist.
        var conversation = await conversationStore.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return TypedResults.NotFound();

        // 1b. No bound schema → nothing to classify against.
        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return TypedResults.Ok(EmptySuggestion);

        // 2. AI disabled for this schema, or Mode == Off → no suggestion (C1: Mode==Off
        //    is now an explicit authority independent of the Enabled flag).
        //    E1 — read the EFFECTIVE config (binding override ?? schema) so a per-binding
        //    pilot can enable/disable AI for one scope without touching the schema default.
        if (!resolved.EffectiveAiConfig.Enabled || resolved.EffectiveAiConfig.Mode == AiMode.Off)
            return TypedResults.Ok(EmptySuggestion);

        // 2b. ADR-0032 — runtime entitlement re-check for platform-managed tenants. The PlatformLlm
        //     entitlement is enforced at opt-in (PUT /admin/ai/llm-config) but can be revoked OUTSIDE
        //     that flow (plan downgrade, add-on expiry, partner-customer suspension, dunning) while the
        //     stored AiSource still says PlatformManaged. FeatureGateService reads the per-request
        //     FeatureGateCache (no TTL — repopulated by TenantStatusMiddleware), so a revocation bites
        //     on the very next request. Placed BEFORE the quota pre-check so a downgraded-and-exhausted
        //     tenant degrades cleanly to 200 (NOT 402) and is NEVER metered (no involuntary billing).
        //     Immediate cutoff (option A); the stored AiSource is NOT mutated, so re-entitlement
        //     self-heals. BYO / no-config tenants are unaffected (guarded by isPlatformManaged).
        if (isPlatformManaged && !featureGate.IsFeatureEnabled(tenantId.Value, PlanFeature.PlatformLlm))
        {
            aiMetrics.PlatformLlmEntitlementMissing.Add(1);
            aiMetrics.LogPlatformLlmEntitlementMissing(tenantId.Value, conversationId.Value);

            await auditService.RecordAsync(
                tenantId,
                category: "config",
                action: "typification.ai.platformllm.entitlement_missing",
                severity: "warning",
                actorId: "system",
                actorType: "system",
                targetId: conversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>
                {
                    ["aiSource"] = AiSource.PlatformManaged.ToString(),
                    ["reason"] = "PlatformLlm entitlement not enabled for tenant at runtime",
                },
                ct: ct);

            return TypedResults.Ok(EmptySuggestion);
        }

        // 3. Load the transcript. Both message stores ORDER BY created_at ASC (oldest
        //    first) and IMessageStore has no newest-first query, so a small limit would
        //    feed the classifier the OLDEST turns; the classifier then keeps only its
        //    internal last-N (40 msgs / 6000 chars). We pull a generous bound so that for
        //    all realistic conversations the true most-recent turns (the most
        //    disposition-relevant context) are included before the internal cap bites. A
        //    dedicated newest-first store query is a future optimization for pathological
        //    (>2000-message) conversations.
        const int TranscriptFetchLimit = 2000;
        var transcript = await messageStore.GetConversationMessagesAsync(tenantId, conversationId, limit: TranscriptFetchLimit, offset: 0, ct);

        // 3a-platform. P2c.2 (C3) — platform-managed AI credit quota pre-check. Only when the tenant
        //     opted into Verbara's operator-managed LLM: a SoftBlock/HardBlock on the monthly AiAnalysis
        //     credit allowance gates the (metered) classify. SoftBlock degrades to the empty suggestion
        //     (AI opt-in floor — same uniform "no suggestion" the client already handles); HardBlock is
        //     surfaced as 402 Payment Required. BYO tenants skip this entirely.
        if (isPlatformManaged)
        {
            var q = await quota.CheckQuotaAsync(tenantId, UsageType.AiAnalysis, additionalQuantity: 1m, ct);
            switch (q.Outcome)
            {
                case QuotaOutcome.HardBlock:
                    return TypedResults.Json(new ErrorResponse("AI credit allowance exhausted."), ApiJsonContext.Default.ErrorResponse, statusCode: 402);
                case QuotaOutcome.SoftBlock:
                    return TypedResults.Ok(EmptySuggestion);
                // Allow + Warn proceed (Warn overflows into PostPaid overage).
            }
        }

        // 3b. E3 — per-tenant daily token budget (fail-closed). When the EFFECTIVE config sets a
        //     DailyTokenBudget and the tenant has ALREADY consumed ≥ that many LLM tokens today
        //     (UTC day), DEGRADE to the empty suggestion WITHOUT calling the LLM (the classify call
        //     is the cost). Gated here — immediately before ClassifyAsync — regardless of AiMode:
        //     Shadow mode still persists suggestions, but the budget controls whether we CALL the LLM,
        //     and the classify is the expensive operation. Increment the fail-closed counter + audit.
        if (resolved.EffectiveAiConfig.DailyTokenBudget is { } dailyBudget &&
            await budget.IsOverBudgetAsync(tenantId, dailyBudget, ct))
        {
            aiMetrics.BudgetExceeded.Add(1);
            aiMetrics.LogBudgetExceeded(tenantId.Value, conversationId.Value, dailyBudget);

            await auditService.RecordAsync(
                tenantId,
                category: "config",
                action: "typification.ai.budget_exceeded",
                severity: "warning",
                actorId: "system",
                actorType: "system",
                targetId: conversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>
                {
                    ["dailyTokenBudget"] = dailyBudget.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["dayUtc"] = clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                },
                ct: ct);

            return TypedResults.Ok(EmptySuggestion);
        }

        // 4. Classify (never throws — degrades to null on no-text / LLM-failure / invalid).
        //    P2c.1 — pass the tenant id FIRST so the classifier resolves THIS tenant's BYO LLM
        //    provider; a tenant with no configured / disabled provider degrades to the empty
        //    suggestion below (the §0 fail-closed seam — AI is strictly opt-in).
        var classification = await classifier.ClassifyAsync(
            EntityId.From(tenantId.Value), resolved.Schema, resolved.SubtreeRoot, conversation, transcript, ct);
        if (classification is null)
            return TypedResults.Ok(EmptySuggestion);

        // 4b. E3 — record the call's token usage against the tenant's UTC-day running sum. The LLM
        //     call happened (classification is non-null), so record once regardless of band/mode —
        //     subsequent calls that push the tenant past DailyTokenBudget will fail-closed above.
        // A provider that returns no usage block records 0 → that call's real cost isn't counted
        // toward the budget (Verbara's OpenAI-compatible provider does populate usage).
        await budget.RecordUsageAsync(tenantId, classification.Usage?.TotalTokens ?? 0, ct);

        // 4c. P2c.2 (C3) — meter the platform-managed classify in AI Credits. ONLY for
        //     platform-managed tenants (BYO is never metered), and only when the provider reported a
        //     usage block. The meter records ONE AiAnalysis/Tokens UsageRecord (total tokens, with
        //     in/out + model in metadata); it is a no-op for non-positive totals. Records regardless
        //     of band/mode — the LLM call happened, so its real cost is billed (mirrors the budget
        //     accounting above).
        if (isPlatformManaged && classification.Usage is { } usage)
        {
            await creditMeter.RecordAsync(
                tenantId, conversationId.Value,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens,
                classification.ModelId, ct);
        }

        // C1 — compute the surfaced band BEFORE persisting, so the stored row records EXACTLY
        // what the agent saw. Calibration excludes AutoFill-band rows (an auto-filled form biases
        // "acceptance"), so the persisted band — not a post-save guess — is the calibration input.
        // E1 — all band/gating decisions below read the EFFECTIVE config (binding override
        // ?? schema), so a single binding can pilot a different automation band.
        var aiConfig = resolved.EffectiveAiConfig;
        var suggestedLeafNodeId = classification.NodePath[^1];

        // Band table (server decides; client MUST NOT escalate):
        //   Mode == Shadow                                             → None  (5  — persisted, never surfaced)
        //   confidence < SuggestThreshold                              → None  (5a)
        //   sentiment-gated Success leaf on very-negative              → None  (5b)
        //   SuggestThreshold ≤ confidence < AutoApplyThreshold        → Suggest (5c)
        //   confidence ≥ AutoApplyThreshold
        //     AND Mode == AutoFill AND calibration.AutoFillReady       → AutoFill (5c)
        //     otherwise (SuggestOnly, or Mode == AutoFill but not ready) → Suggest (5c — downgrade)
        TypificationBand surfacedBand = TypificationBand.None;
        if (aiConfig.Mode == AiMode.Shadow)
        {
            // 5 — Shadow mode: persisted but surface nothing to the agent.
            surfacedBand = TypificationBand.None;
        }
        else if (classification.Confidence < aiConfig.SuggestThreshold)
        {
            // 5a. Confidence gate: below SuggestThreshold → no suggestion at all (Band.None).
            surfacedBand = TypificationBand.None;
        }
        else if (aiConfig.SentimentGating &&
                 string.Equals(classification.Sentiment, "very_negative", StringComparison.OrdinalIgnoreCase) &&
                 classification.NodePath.Count > 0 &&
                 resolved.Schema.Nodes.FirstOrDefault(n => n.NodeId == suggestedLeafNodeId)?.Leaf?.Category
                     == TypificationCategory.Success)
        {
            // 5b. Sentiment gate: never surface a Success outcome on a very-negative call.
            surfacedBand = TypificationBand.None;
        }
        else if (classification.Confidence < aiConfig.AutoApplyThreshold ||
                 aiConfig.Mode != AiMode.AutoFill)
        {
            // 5c. Below auto-apply, or mode never auto-fills → Suggest.
            surfacedBand = TypificationBand.Suggest;
        }
        else
        {
            // 5c. confidence ≥ AutoApplyThreshold AND Mode == AutoFill — check calibration ONLY on
            //     this AutoFill-eligible branch (preserve round-trip economy for the other paths).
            var status = await calibration.GetStatusAsync(tenantId, resolved.Schema.SchemaId, ct);
            surfacedBand = status.AutoFillReady ? TypificationBand.AutoFill : TypificationBand.Suggest;
        }

        // B2 — persist every non-null classification, regardless of mode or gating, WITH the band
        // actually surfaced. The leaf is always the last element of the validated NodePath.
        var record = new AiSuggestionRecord
        {
            Id = EntityId.New(),
            TenantId = EntityId.From(tenantId.Value),
            ConversationId = conversationId,
            SchemaId = resolved.Schema.SchemaId,
            SchemaVersion = resolved.Schema.Version,
            SuggestedLeafNodeId = suggestedLeafNodeId,
            SuggestedNodePath = classification.NodePath.Select(n => n.Value).ToArray(),
            SuggestedFieldValues = classification.FieldValues,
            Confidence = classification.Confidence,
            Sentiment = classification.Sentiment,
            ModelId = classification.ModelId,
            PromptVersion = classification.PromptVersion,
            SurfacedBand = surfacedBand,
            CreatedAt = clock.UtcNow,
            CommittedLeafNodeId = null,
            Accepted = null,
        };

        await aiSuggestions.SaveAsync(record, ct);
        aiMetrics.SuggestionMade.Add(1);
        aiMetrics.LogSuggestionPersisted(
            suggestionId: record.Id.Value,
            conversationId: conversationId.Value,
            leafNodeId: suggestedLeafNodeId.Value,
            confidence: classification.Confidence,
            mode: aiConfig.Mode.ToString());

        // Response Band == persisted SurfacedBand. None → empty payload (shadow / below-threshold /
        // sentiment-gated all collapse here, preserving every prior externally-observable behavior).
        if (surfacedBand == TypificationBand.None)
            return TypedResults.Ok(EmptySuggestion);

        return TypedResults.Ok(new TypificationSuggestionResponse(
            SuggestedNodePath: classification.NodePath.Select(n => n.Value).ToArray(),
            SuggestedFieldValues: classification.FieldValues.Count > 0 ? classification.FieldValues : null,
            Confidence: classification.Confidence,
            Sentiment: classification.Sentiment,
            Band: surfacedBand));
    }

    /// <summary>All-null "no suggestion" payload reused by every degrade path.</summary>
    private static readonly TypificationSuggestionResponse EmptySuggestion =
        new(SuggestedNodePath: null, SuggestedFieldValues: null, Confidence: null, Sentiment: null);

    // The owner, or a supervisor (audited with by_supervisor), types and wraps up a conversation. The
    // submission is attributed to the calling user, as before.
    private static async Task<Results<Ok<TypificationSubmission>, NotFound, BadRequest<ErrorResponse>, BadRequest<TypifyErrorResponse>, JsonHttpResult<ErrorResponse>>> TypifyConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] IAgentStore agents,
        [FromServices] ITypificationResolver resolver,
        [FromServices] ITypificationValidator validator,
        [FromServices] ITypificationSubmissionStore submissionStore,
        [FromServices] ITypificationProvenanceService provenanceService,
        [FromServices] IAuditService auditService,
        [FromServices] IConversationSwitchboard switchboard,
        CampaignStoreBase campaignStore,
        DispositionCodeStoreBase dispositionCodeStore,
        PlatformEventBus eventBus,
        IClock clock,
        [FromBody] TypifyRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = EntityId.From(CallerIdentity.ResolveUserIdOrSystem(context.User));
        var conversationId = EntityId.From(id);

        // 1. Load conversation + resolve the bound published schema.
        var conversation = await conversationStore.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        var bySupervisor = !actor.Owns(conversation);
        if (bySupervisor && !await ConversationActor.IsSupervisorAsync(context))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);
        var owner = conversation.Owner;

        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return TypedResults.BadRequest(new ErrorResponse("no typification schema bound"));

        var schema = resolved.Schema;

        // 2. Map the selected node-path strings → EntityId list.
        var path = body.SelectedNodePath.Select(EntityId.From).ToList();

        // 3a. Derive the server-authoritative AI Source READ-ONLY BEFORE validation so validation
        //     (D3: AI free-text length cap) and the PII screen are Source-aware WITHOUT any
        //     mutation. This is a pure read — it does NOT reconcile the suggestion or touch
        //     metrics (the bug D3 introduced: DeriveAsync, which DOES reconcile + bump metrics,
        //     ran here before validation, so an invalid submission still marked the suggestion
        //     accepted, polluting calibration accuracy). The full reconciling derive (DeriveAsync)
        //     now runs only after the disposition is committed (step 4a). Empty-path guard: only
        //     derive when a leaf exists; an empty path collapses to Manual (validation rejects the
        //     empty path below anyway).
        //     Note: the latest suggestion is read twice — once here (read-only) and once in
        //     DeriveAsync post-persist. That's an acceptable cost (a cheap indexed read).
        var aiSource = path.Count > 0
            ? await provenanceService.DeriveSourceAsync(tenantId, conversationId, path[^1], ct)
            : SubmissionSource.Manual;

        // 3b. Server-authoritative validation of the submission FIRST — a validation
        //     failure must NOT mutate or persist any conversation state, and (post-D3-fix)
        //     must NOT reconcile any AI suggestion.
        var validation = validator.ValidateSubmission(schema, path, body.FieldValues, aiSource);
        if (!validation.IsValid)
        {
            return TypedResults.BadRequest(new TypifyErrorResponse(
                validation.Errors.Select(e => new TypifyFieldError(e.Field, e.Message)).ToArray()));
        }

        // 4. Move to WrapUp only after validation succeeds (close-chat-without-typification-licence D6).
        //    Agent-held digital work goes through the switchboard: a state-and-owner conditional commit that
        //    releases the owner's capacity unit once, only when it wins (D2/D3). Voice keeps its own path — the voice
        //    bridge owns that release (D8) — as does a conversation no agent holds (no unit to release).
        //    Either way the state is checked before any mutation, never with try/catch, and a refusal or a
        //    lost race is a 409 conversation-not-closable (D9).
        //    The wrap-up is predicated on the owner the actor check was decided against (`owner`, read before
        //    the resolver ran): a transfer that committed since is a 409, never a submission attributed to
        //    the old owner on the new owner's chat (D3).
        if (conversation.Channel != ChannelType.Voice
            && owner is { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerAgentId })
        {
            var wrapUp = await switchboard.WrapUpAsync(tenantId, conversationId, ownerAgentId, ct);
            if (!wrapUp.Success)
                return ConversationActor.Conflict(ConversationActor.NotClosable);
        }
        else
        {
            if (!ConversationStateMachine.CanTransition(conversation.State, ConversationState.WrapUp))
                return ConversationActor.Conflict(ConversationActor.NotClosable);

            conversation.TransitionTo(ConversationState.WrapUp);
            await conversationStore.SaveAsync(conversation, ct);
        }

        var leafNodeId = path[^1];

        // 4a. Derive the FULL server-authoritative AI provenance (B3) NOW — only after validation
        //     succeeded AND the disposition is committed (WrapUp + SaveAsync). DeriveAsync is the
        //     MUTATING derive: it reconciles the stored suggestion (MarkReconciledAsync) and bumps
        //     the accept/override metrics, so it MUST fire exactly once and only on a committed
        //     disposition — never on a rejected submission (that was the D3 regression). The
        //     persisted submission below uses this provenance.* plus the screened fieldValues.
        //     This re-reads the latest suggestion (already read read-only in step 3a) — an
        //     acceptable cheap indexed read.
        var provenance = path.Count > 0
            ? await provenanceService.DeriveAsync(tenantId, conversationId, leafNodeId, ct)
            : new ProvenanceResult();

        // 4a (cont.). PII screen (D3, defense-in-depth): on the AI write path re-screen free-text
        //     field values (Text/Textarea/Lookup ONLY — a Phone field legitimately holds a phone)
        //     before persisting. D2 screens at extraction; this re-screens at commit. Manual
        //     submissions persist the captured values unchanged. Uses the read-only-derived
        //     aiSource (identical to provenance.Source) decided pre-validation.
        var fieldValues = aiSource == SubmissionSource.AutoAi
            ? ScreenFreeTextPii(schema, resolved.EffectiveAiConfig, body.FieldValues)
            : body.FieldValues;

        // 4b. Persist the submission.
        var submission = new TypificationSubmission
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            AgentId = agentId,
            SchemaId = schema.SchemaId,
            SchemaVersion = schema.Version,
            SelectedNodePath = path,
            LeafNodeId = leafNodeId,
            FieldValues = fieldValues,
            // I2 — Notes is human-authored wrap-up free text (never AI-extracted), so it is
            // intentionally NOT PII-screened even on the AutoAi path. Only AI-sourced free-text
            // field values (Text/Textarea/Lookup, via ScreenFreeTextPii) are screened.
            Notes = body.Notes,
            AiSuggested = provenance.AiSuggested,
            AiConfidence = provenance.AiConfidence,
            AiAccepted = provenance.AiAccepted,
            Source = provenance.Source,
            SuggestedLeafNodeId = provenance.SuggestedLeafNodeId,
            SuggestedNodePath = provenance.SuggestedNodePath,
            Duration = TimeSpan.Zero,
            CompletedAt = clock.UtcNow,
        };

        await submissionStore.SaveAsync(submission, ct);

        // 4c. Emit AI-disposition audit entry (GDPR Art. 22 traceability — B4b).
        //     Only when an AI suggestion was involved (accepted or overridden).
        //     The committing actor is always the human agent in this phase; the AI model
        //     details are recorded in the audit metadata as provenance, not as the actor.
        if (provenance.AiSuggested)
        {
            await auditService.RecordAsync(
                tenantId,
                category: "conversations",
                action: "typification.ai_disposition",
                severity: "info",
                actorId: agentId.Value,
                actorType: "user",
                targetId: conversationId.Value,
                targetType: "Conversation",
                metadata: new Dictionary<string, string>
                {
                    ["modelId"] = provenance.ModelId ?? string.Empty,
                    ["promptVersion"] = provenance.PromptVersion ?? string.Empty,
                    ["schemaVersion"] = provenance.SchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    ["confidence"] = provenance.AiConfidence?.ToString("G", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    ["suggestedLeafNodeId"] = provenance.SuggestedLeafNodeId?.Value ?? string.Empty,
                    ["committedLeafNodeId"] = leafNodeId.Value,
                    ["accepted"] = (provenance.AiAccepted ?? false).ToString().ToLowerInvariant(),
                },
                ct: ct);
        }

        // 5. Dialer bridge (PRESERVED behavior): a leaf node may carry a DialerCode
        //    that maps to a Pro campaign DispositionCode for the outbound call attempt.
        var leaf = schema.Nodes.FirstOrDefault(n => n.NodeId == leafNodeId)?.Leaf;
        if (leaf?.DialerCode is { } dialerCode)
        {
            var meta = conversation.Metadata;
            if (meta.TryGetValue("callAttemptId", out var attemptIdStr) &&
                long.TryParse(attemptIdStr, out var callAttemptId) &&
                meta.TryGetValue("campaignId", out var campIdStr) &&
                long.TryParse(campIdStr, out var campaignId))
            {
                var tenantStr = tenantId.Value;
                var agentSubId = context.User.FindFirst("sub")?.Value ?? "";

                var dispositions = await dispositionCodeStore.ListByCampaignAsync(tenantStr, campaignId, ct);
                var dispo = dispositions.FirstOrDefault(d => d.Code == dialerCode);

                if (dispo is not null)
                {
                    // I1/M4 — the dialer bridge intentionally reads RAW body.* (the
                    // disposition note here, the callback date below): the disposition note is
                    // the human-authored wrap-up note (not AI-extracted), and callback_date is
                    // a Date field, never a free-text field, so neither is PII-screened.
                    await campaignStore.UpdateCallAttemptDispositionAsync(
                        tenantStr, callAttemptId, dispo.Id, body.Notes, ct);
                }

                // Schedule a callback when the leaf requests one and a valid date was captured.
                if (leaf.TriggerCallback &&
                    body.FieldValues.TryGetValue(TypificationFieldKeys.CallbackDate, out var cbDate) &&
                    DateTimeOffset.TryParse(
                        cbDate,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var scheduledAt) &&
                    meta.TryGetValue("contactId", out var contactIdStr) &&
                    long.TryParse(contactIdStr, out var contactId))
                {
                    await campaignStore.SaveCallbackAsync(
                        tenantStr, campaignId, contactId, scheduledAt.ToUtcInstant(), agentSubId, ct);
                }

                eventBus.Publish(new CampaignDispositionSubmittedEvent(
                    tenantStr, campaignId, dispo?.Code ?? "", agentSubId));
            }
        }

        // 6. Broadcast the typification submission (cross-pod via the event bus).
        eventBus.Publish(new TypificationSubmittedEvent(
            tenantId.Value,
            conversationId.Value,
            schema.SchemaId.Value,
            schema.Version,
            leafNodeId.Value,
            agentId.Value));

        if (bySupervisor)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConversationAudit.BySupervisor] = agentId.Value,
                ["leaf_node_id"] = leafNodeId.Value,
            };
            ConversationActor.AddOwner(metadata, "owner", owner);
            await ConversationAudit.TryRecordAsync(
                context, auditService, tenantId, "conversation.typified", conversationId, metadata, ct);
        }

        return TypedResults.Ok(submission);
    }

    /// <summary>
    /// Builds a PII-screened copy of <paramref name="fieldValues"/> for the AI write path (D3).
    /// Only free-text field values (Text/Textarea/Lookup) are screened via
    /// <see cref="PiiScreen.Apply(string, PiiPolicy?)"/> under the EFFECTIVE
    /// <see cref="TypificationAiConfig.PiiPolicy"/> (E1 — the binding override's policy when
    /// present, else the schema's). A <c>Phone</c> field legitimately holds a phone number and is
    /// left untouched (masking it would break it). Entries whose key has no matching schema field,
    /// and all non-free-text fields, are copied through unchanged.
    /// <para>
    /// E1 scope note (extraction is NOT override-aware): the classifier still EXTRACTS under the
    /// SCHEMA's AiConfig (its EntityFieldMap + its extraction-time PII), so a per-binding override's
    /// map / PII allow-list is NOT honored at extraction. The binding override's PiiPolicy drives the
    /// WRITE-PATH re-screen (this method), and its band / mode drives suggestion gating — but a
    /// TIGHTENING override does NOT re-mask at the suggestion SURFACE (only here at persist), and a
    /// LOOSENING override over-masks at extraction (fail-safe: extraction-time masking under the
    /// schema policy can only ever be more restrictive than a looser override would allow).
    /// See "E1-ext (follow-up)" in docs/plans/active/2026-06-14-typification-p2b-autoapply-safe.md.
    /// </para>
    /// </summary>
    private static Dictionary<string, string> ScreenFreeTextPii(
        TypificationSchema schema,
        TypificationAiConfig effectiveAiConfig,
        IReadOnlyDictionary<string, string> fieldValues)
    {
        var fieldsByKey = new Dictionary<string, TypificationField>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
            fieldsByKey[field.Key] = field;

        var screened = new Dictionary<string, string>(fieldValues.Count, StringComparer.Ordinal);
        foreach (var (key, value) in fieldValues)
        {
            if (fieldsByKey.TryGetValue(key, out var field)
                && field.Type.IsFreeText())
            {
                screened[key] = PiiScreen.Apply(value, effectiveAiConfig.PiiPolicy).Value;
            }
            else
            {
                screened[key] = value;
            }
        }

        return screened;
    }

    // The refusal code of a channel name that names no channel (D14): stable, with no echo of the request.
    private const string UnknownChannel = "unknown-channel";

    // With an initial message the caller starts the conversation in its own name (agent-started-conversations,
    // design D14): the starter sends first, persists nothing on failure, and a success is born Active and owned by
    // the caller's Agent profile. Without one, the 2.26 queued path stays — deprecated, removal planned for a later release.
    private static async Task<Results<Created<Conversation>, NotFound<ErrorResponse>,
        JsonHttpResult<ErrorResponse>, BadRequest<ConversationStartRefusal>, Conflict<ConversationStartRefusal>,
        JsonHttpResult<ConversationStartRefusal>>> CreateConversation(
        HttpContext context,
        [FromBody] CreateConversationRequest body,
        IConversationService conversationService,
        [FromServices] IOutboundConversationStarter starter,
        [FromServices] IContactStore contactStore,
        [FromServices] IAgentStore agents,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // A stable code, never the caller's input; Enum.TryParse also takes undefined numbers such as "99".
        if (!Enum.TryParse<ChannelType>(body.Channel, ignoreCase: true, out var channelType) || !Enum.IsDefined(channelType))
            return TypedResults.BadRequest(new ConversationStartRefusal(UnknownChannel));

        // An initial message is sent as the caller's Agent profile, so a caller without one cannot send it.
        ConversationActor? actor = null;
        if (body.InitialMessage is not null)
        {
            actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
            if (actor.Agent is null)
                return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        }

        var contactId = EntityId.From(body.ContactId);

        var contact = await contactStore.GetByIdAsync(tenantId, contactId, ct);
        if (contact is null)
            return TypedResults.NotFound(new ErrorResponse("Contact not found"));

        if (actor is not { Agent: { } agent, UserId: { } actorUserId })
        {
            // Deprecated (D14): the queued path without an initial message; removal planned for a later release.
            var queued = await conversationService.GetOrCreateForContactAsync(tenantId, contactId, channelType, ct);
            return TypedResults.Created($"/conversations/{queued.ConversationId.Value}", queued);
        }

        var outcome = await starter.StartAsync(
            new OutboundStartRequest(
                tenantId, contactId, channelType, agent.AgentId, actorUserId,
                new MessageEnvelope([new TextBlock(body.InitialMessage!)]),
                string.IsNullOrWhiteSpace(body.TemplateName) ? null : body.TemplateName,
                string.IsNullOrWhiteSpace(body.TemplateLanguage) ? null : body.TemplateLanguage),
            ct);

        if (outcome.RefusalCode is { } code)
            return StartRefusal(code, outcome);

        var conversation = outcome.Conversation!;
        if (!outcome.Reused)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent_id"] = agent.AgentId.Value,
                ["channel"] = channelType.ToString(),
                ["message_id"] = outcome.Message!.MessageId.Value,
            };
            await ConversationAudit.TryRecordAsync(
                context, audit, tenantId, "conversation.started", conversation.ConversationId, metadata, ct);
        }

        return TypedResults.Created($"/conversations/{conversation.ConversationId.Value}", conversation);
    }

    // The status each start refusal carries (D14): 400 for a contact with no address, 502 when the provider
    // rejected the first message, 500 when the sent message could not be stored, 409 for every other refusal
    // decided before the send.
    private static Results<Created<Conversation>, NotFound<ErrorResponse>,
        JsonHttpResult<ErrorResponse>, BadRequest<ConversationStartRefusal>, Conflict<ConversationStartRefusal>,
        JsonHttpResult<ConversationStartRefusal>> StartRefusal(string code, OutboundStartOutcome outcome)
    {
        var refusal = new ConversationStartRefusal(
            code,
            outcome.OpenConversationId?.Value,
            outcome.OpenConversationId is null ? null : outcome.OpenOwnerKind?.ToString() ?? "None");

        return code switch
        {
            OutboundStartCodes.ContactHasNoAddress => TypedResults.BadRequest(refusal),
            OutboundStartCodes.ChannelSendFailed => TypedResults.Json(
                refusal, ApiJsonContext.Default.ConversationStartRefusal, statusCode: StatusCodes.Status502BadGateway),
            OutboundStartCodes.StartPersistFailed => TypedResults.Json(
                refusal, ApiJsonContext.Default.ConversationStartRefusal, statusCode: StatusCodes.Status500InternalServerError),
            _ => TypedResults.Conflict(refusal),
        };
    }

    // Only the owner holds or resumes a conversation, as its Agent profile.
    private static async Task<Results<Ok<OwnershipResult>, NotFound, BadRequest<ErrorResponse>, JsonHttpResult<ErrorResponse>>> HoldConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        if (!actor.Owns(conversation))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);

        var result = await switchboard.HoldAsync(conversation.ConversationId, tenantId, actor.Agent.AgentId, ct);

        return result.Success
            ? TypedResults.Ok(result)
            : TypedResults.BadRequest(new ErrorResponse(result.FailureReason ?? "Cannot hold conversation"));
    }

    private static async Task<Results<Ok<OwnershipResult>, NotFound, BadRequest<ErrorResponse>, JsonHttpResult<ErrorResponse>>> UnholdConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromServices] IConversationStore conversations,
        [FromServices] IAgentStore agents,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await conversations.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return TypedResults.NotFound();

        var actor = await ConversationActor.ResolveAsync(context, tenantId, agents, ct);
        if (actor.Agent is null)
            return ConversationActor.Forbidden(ConversationActor.NotAnAgent);
        if (!actor.Owns(conversation))
            return ConversationActor.Forbidden(ConversationActor.NotOwner);

        var result = await switchboard.UnholdAsync(conversation.ConversationId, tenantId, actor.Agent.AgentId, ct);

        return result.Success
            ? TypedResults.Ok(result)
            : TypedResults.BadRequest(new ErrorResponse(result.FailureReason ?? "Cannot unhold conversation"));
    }

    // ADR-0034 — POST /conversations/{id}/typification-correction. A supervisor holding the
    // `typification:correct-autonomous` permission (enforced on the route) corrects an autonomously
    // stamped disposition. The original AutoAi submission stays byte-identical: the human path is
    // written to a SEPARATE append-only correction record; only the submission's CorrectionState /
    // CorrectedAt (status pointers) are flipped. The conversation is NOT reopened. All three guards
    // return 409 with a machine-readable code BEFORE any write.
    //
    // audit-trail-integrity-fixes (fix 1) — the correction record, the submission's flipped status
    // pointers, and the audit record are committed ATOMICALLY via ITypificationCorrectionAuditWriter
    // (a single Postgres transaction / lock-scoped InMemory compound), closing the previous 2-write
    // window where a fault between the correction write and the audit write could leave a
    // correction with no audit trail (or vice versa).
    private static async Task<Results<Ok<TypificationCorrectionResponse>, BadRequest<ErrorResponse>, NotFound, JsonHttpResult<TypificationCorrectionErrorResponse>>> CorrectTypification(
        string id,
        HttpContext context,
        [FromServices] ITypificationSubmissionStore submissionStore,
        [FromServices] ITypificationCorrectionAuditWriter correctionAuditWriter,
        [FromServices] AutonomousDispositionMetrics metrics,
        [FromServices] IOptions<DistributionOptions> options,
        [FromBody] TypificationCorrectionRequest body,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversationId = EntityId.From(id);
        var callerUserId = GetCorrectingUserId(context);

        // Body validation (BadRequest, not a 409 guard): a corrected leaf/path is required.
        if (body.CorrectedNodePath is not { Count: > 0 })
            return TypedResults.BadRequest(new ErrorResponse("correctedNodePath must be a non-empty root→leaf path"));

        var submission = await submissionStore.GetByConversationIdAsync(tenantId, conversationId, ct);
        if (submission is null)
            return TypedResults.NotFound();

        // ── Guards (all BEFORE any write; each returns 409 + a machine-readable code) ──

        // 1. Only an autonomously stamped (AutoAi) disposition is correctable via this path.
        if (submission.Source != SubmissionSource.AutoAi)
            return Conflict(TypificationCorrectionErrorCodes.NotAutonomous,
                "Only an autonomously stamped (AutoAi) disposition can be corrected here.");

        // 2. The correction window must not have elapsed (CompletedAt + window ≥ now).
        var windowEnd = submission.CompletedAt.AddDays(options.Value.AutonomousCorrectionWindowDays);
        if (clock.UtcNow > windowEnd)
            return Conflict(TypificationCorrectionErrorCodes.CorrectionWindowExpired,
                "The correction window for this autonomous disposition has expired.");

        // 3. A conversation is correctable at most once.
        if (submission.CorrectionState != CorrectionState.None)
            return Conflict(TypificationCorrectionErrorCodes.AlreadyCorrected,
                "This autonomous disposition has already been corrected.");

        // ── Effect (all guards passed) ──

        var now = clock.UtcNow;
        var correctedPath = body.CorrectedNodePath.Select(EntityId.From).ToList();
        var correctedLeaf = correctedPath[^1];

        // (a) The SEPARATE append-only correction record (the human path).
        var correction = new TypificationSubmissionCorrection
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            CorrectedLeafNodeId = correctedLeaf,
            CorrectedNodePath = correctedPath,
            CorrectedByUserId = callerUserId,
            CorrectedAt = now,
        };

        // (b) Flip ONLY the submission's status pointers. The AI leaf, path, confidence, and
        //     Source stay byte-identical — the recorded AI decision is immutable;
        //     CorrectionState/CorrectedAt only mark it superseded.
        var corrected = submission with
        {
            CorrectionState = CorrectionState.Corrected,
            CorrectedAt = now,
        };

        // The conversation stays Closed — NO reopen (ADR-0034 Decision 5).

        // (c) The DispositionCorrected audit entry (actor = supervisor user), recording the
        //     original AI path and the corrected path, with Confirmed = (original == corrected).
        //     Retention floor mirrors the commit's: now + correction window. Built directly (not
        //     via IAuditService.RecordAsync) — including the v2 integrity hash covering
        //     RetainUntil (audit-trail-integrity-fixes, fix 4) — so it can be persisted inside the
        //     SAME transaction as the correction + submission writes.
        var originalPathStr = string.Join(" > ", submission.SelectedNodePath.Select(n => n.Value));
        var correctedPathStr = string.Join(" > ", correctedPath.Select(n => n.Value));
        var confirmed = string.Equals(originalPathStr, correctedPathStr, StringComparison.Ordinal);
        var retainUntil = now.AddDays(options.Value.AutonomousCorrectionWindowDays);
        var auditMetadata = new Dictionary<string, string>
        {
            ["original_node_path"] = originalPathStr,
            ["corrected_node_path"] = correctedPathStr,
            ["confirmed"] = confirmed ? "true" : "false",
            ["tenant"] = tenantId.Value,
            ["conversation"] = conversationId.Value,
        };
        var auditEntry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            Action = "typification.autonomous.corrected",
            Category = "conversations",
            Severity = "info",
            ActorId = callerUserId,
            ActorType = "user",
            TargetId = conversationId.Value,
            TargetType = "Conversation",
            Metadata = auditMetadata,
            OccurredAt = now,
            RetainUntil = retainUntil,
            IntegrityHash = DefaultAuditService.ComputeIntegrityHashV2(
                tenantId, actorType: "user", actorId: callerUserId, action: "typification.autonomous.corrected",
                targetType: "Conversation", targetId: conversationId.Value, occurredAt: now,
                retainUntil: retainUntil, metadata: auditMetadata),
        };

        // (d) Commit correction + submission + audit as ONE atomic unit.
        await correctionAuditWriter.CommitAsync(correction, corrected, auditEntry, ct);

        // (e) Metric (tenant-dimensioned).
        metrics.RecordCorrection(tenantId.Value);

        return TypedResults.Ok(new TypificationCorrectionResponse(
            ConversationId: conversationId.Value,
            CorrectedNodePath: correctedPath.Select(n => n.Value).ToArray(),
            CorrectedLeafNodeId: correctedLeaf.Value,
            CorrectedByUserId: callerUserId,
            CorrectedAt: now,
            Confirmed: confirmed));
    }

    // Machine-readable 409 for the correction guards.
    private static JsonHttpResult<TypificationCorrectionErrorResponse> Conflict(string code, string message) =>
        TypedResults.Json(
            new TypificationCorrectionErrorResponse(code, message),
            ApiJsonContext.Default.TypificationCorrectionErrorResponse,
            statusCode: StatusCodes.Status409Conflict);

    // The correcting supervisor's user id via the shared canonical resolver (same order as the
    // permission handler: user_id ?? NameIdentifier ?? sub). Falls back to "system" so the audit
    // records an actor.
    private static string GetCorrectingUserId(HttpContext context) =>
        CallerIdentity.ResolveUserIdOrSystem(context.User);

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

internal sealed record SendMessageRequest(string Text);
internal sealed record TransferRequest(string? TargetQueueId, string? TargetAgentId);

/// <summary>
/// Runtime typification submission: the selected root→leaf node path, the captured
/// field values (key → value, typed-validated server-side), optional free-text notes,
/// and AI provenance (P2 — null when no AI involved): whether the chosen path originated
/// from an AI suggestion (<paramref name="AiSuggested"/> → <c>Source = AutoAi</c>), the
/// suggestion's confidence (<paramref name="AiConfidence"/>), and whether the agent
/// accepted the suggestion verbatim (<paramref name="AiAccepted"/>).
/// </summary>
internal sealed record TypifyRequest(
    IReadOnlyList<string> SelectedNodePath,
    IReadOnlyDictionary<string, string> FieldValues,
    string? Notes = null,
    bool? AiAccepted = null,
    bool? AiSuggested = null,
    double? AiConfidence = null);

/// <summary>
/// D1 — AI auto-disposition suggestion (P2a/C1). The suggested root→leaf node-id path,
/// optional captured field values, the model's confidence, a free-form sentiment hint,
/// and the server-authoritative delivery <see cref="TypificationBand"/>. All members are
/// <see langword="null"/> / <see cref="TypificationBand.None"/> (the all-null payload)
/// when there is no suggestion — no bound schema, AI disabled, the classifier degraded,
/// the confidence fell below the schema threshold, or a sentiment-gated Success outcome
/// was suppressed.
/// </summary>
internal sealed record TypificationSuggestionResponse(
    IReadOnlyList<string>? SuggestedNodePath,
    IReadOnlyDictionary<string, string>? SuggestedFieldValues,
    double? Confidence,
    string? Sentiment,
    TypificationBand Band = TypificationBand.None);

/// <summary>
/// The resolved typification form for a conversation: the cascading schema, the
/// optional sub-tree root, and the wrap-up PREFILL (C9) — a preselected reason node
/// path (root→leaf node-id strings) and prefilled field values (keyed by field Key)
/// derived from the conversation's captured context so the agent confirms instead of
/// re-classifying. Both prefill members are <see langword="null"/> (not empty) when
/// nothing is preselectable, so the client cleanly distinguishes "no prefill".
/// </summary>
internal sealed record TypificationFormResponse(
    TypificationSchemaDto Schema,
    string? SubtreeRootNodeId,
    IReadOnlyList<string>? PrefilledNodePath = null,
    IReadOnlyDictionary<string, string>? PrefilledFieldValues = null);

/// <summary>400 payload when a runtime typify submission fails server validation.</summary>
internal sealed record TypifyErrorResponse(IReadOnlyList<TypifyFieldError> Errors);

internal sealed record TypifyFieldError(string Field, string Message);

/// <summary>Well-known <see cref="TypifyRequest.FieldValues"/> keys recognized by the runtime typify endpoint.</summary>
internal static class TypificationFieldKeys
{
    /// <summary>Field key whose value (a parseable <see cref="DateTimeOffset"/>) schedules a dialer callback.</summary>
    internal const string CallbackDate = "callback_date";
}

/// <summary>
/// Body of <c>POST /conversations</c>. With <c>initialMessage</c> the caller starts the conversation in its own name
/// (design D14); <c>templateName</c> and <c>templateLanguage</c> send a provider template instead (WhatsApp outside
/// its 24-hour window). Without <c>initialMessage</c> the deprecated queued path runs.
/// </summary>
internal sealed record CreateConversationRequest(
    string ContactId,
    string Channel,
    string? InitialMessage = null,
    string? TemplateName = null,
    string? TemplateLanguage = null);

/// <summary>
/// A refused agent start (design D14): <c>error</c> is the stable code. For <c>conversation-open-elsewhere</c> and
/// <c>conversation-not-sendable</c> it names the open conversation and its owner kind (<c>Agent</c>,
/// <c>Queue</c>, <c>Bot</c>, <c>System</c>, or <c>None</c> when queued unowned).
/// </summary>
internal sealed record ConversationStartRefusal(string Error, string? ConversationId = null, string? OwnerKind = null);

// ─── ADR-0034 — supervisor correction of an autonomous disposition ─────────────

/// <summary>
/// Request body for POST /conversations/{id}/typification-correction: the supervisor's corrected
/// root→leaf node-id path. The last element is the corrected leaf. The correcting user id is taken
/// from the caller's credentials, not the body.
/// </summary>
internal sealed record TypificationCorrectionRequest(
    IReadOnlyList<string> CorrectedNodePath);

/// <summary>
/// 200 payload for a successful correction: echoes the persisted (separate, append-only) correction
/// record and whether the human path merely CONFIRMED the AI path (<c>original == corrected</c>).
/// </summary>
internal sealed record TypificationCorrectionResponse(
    string ConversationId,
    IReadOnlyList<string> CorrectedNodePath,
    string CorrectedLeafNodeId,
    string CorrectedByUserId,
    DateTimeOffset CorrectedAt,
    bool Confirmed);

/// <summary>
/// 409 payload for a rejected correction. <see cref="Code"/> is machine-readable
/// (see <see cref="TypificationCorrectionErrorCodes"/>).
/// </summary>
internal sealed record TypificationCorrectionErrorResponse(string Code, string Message);

/// <summary>Machine-readable 409 error codes for the correction endpoint (ADR-0034).</summary>
internal static class TypificationCorrectionErrorCodes
{
    /// <summary>The submission is not an autonomously stamped (AutoAi) disposition.</summary>
    internal const string NotAutonomous = "NotAutonomous";

    /// <summary>The bounded correction window has elapsed.</summary>
    internal const string CorrectionWindowExpired = "CorrectionWindowExpired";

    /// <summary>The disposition has already been corrected once.</summary>
    internal const string AlreadyCorrected = "AlreadyCorrected";
}
