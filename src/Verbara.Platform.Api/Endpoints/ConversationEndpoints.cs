using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Switchboard;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;
using Verbara.Platform.Typification.Validation;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Dispositions;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

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
        group.MapPost("/{id}/transfer", TransferConversation);
        group.MapPost("/{id}/close", CloseConversation);
        group.MapGet("/{id}/typification-form", GetTypificationForm);
        group.MapPost("/{id}/typify", TypifyConversation);
        // D1 — AI auto-disposition suggestion. The conversations group is NOT
        // license-gated (agents must always be able to close work), so the gate is
        // applied to THIS endpoint only, requiring BOTH AdvancedTypification AND
        // TypificationAi: the LicenseGateMiddleware reads one LicenseFeatureMetadata and
        // checks HasFlag, so a combined flags value gates on EVERY set bit (402 unless both).
        group.MapPost("/{id}/typification-suggestion", GetTypificationSuggestion)
            .RequireLicenseFeature(LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi);
        group.MapPost("/{id}/hold", HoldConversation);
        group.MapPost("/{id}/unhold", UnholdConversation);
        group.MapPost("/", CreateConversation);
    }

    private static async Task<IResult> ListConversations(
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
        return Results.Ok(result);
    }

    private static async Task<IResult> GetConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return conversation is null ? Results.NotFound() : Results.Ok(conversation);
    }

    private static async Task<IResult> GetMessages(
        string id,
        HttpContext context,
        [FromServices] IMessageStore store,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var messages = await store.GetConversationMessagesAsync(tenantId, EntityId.From(id), limit, offset, ct);
        return Results.Ok(messages);
    }

    private static async Task<IResult> SendMessage(
        string id,
        HttpContext context,
        [FromBody] SendMessageRequest body,
        IConversationService conversationService,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);

        var envelope = new MessageEnvelope(
        [
            new TextBlock(body.Text),
        ]);

        var message = await conversationService.SendMessageAsync(
            EntityId.From(id),
            tenantId,
            envelope,
            agentId,
            ConversationOwnerKind.Agent,
            ct);

        return Results.Ok(message);
    }

    private static async Task<IResult> AcceptConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.AcceptAsync(EntityId.From(id), tenantId, agentId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RejectConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.RejectAsync(EntityId.From(id), tenantId, agentId, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> TransferConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        [FromBody] TransferRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        OwnershipResult result;

        if (body.TargetQueueId is not null)
            result = await switchboard.TransferToQueueAsync(EntityId.From(id), tenantId, EntityId.From(body.TargetQueueId), ct);
        else if (body.TargetAgentId is not null)
            result = await switchboard.TransferToAgentAsync(EntityId.From(id), tenantId, EntityId.From(body.TargetAgentId), ct);
        else
            return Results.BadRequest(new ErrorResponse("Either targetQueueId or targetAgentId must be specified"));

        return Results.Ok(result);
    }

    private static async Task<IResult> CloseConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversation = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (conversation is null)
            return Results.NotFound();

        conversation.TransitionTo(ConversationState.Closed);
        await store.SaveAsync(conversation, ct);
        return Results.Ok(conversation);
    }

    private static async Task<IResult> GetTypificationForm(
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
            return Results.NotFound();

        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return Results.NotFound();

        // C9 — resolve the wrap-up prefill (preselected reason path + prefilled field
        // values) from the conversation's captured context. Pure + never throws; empty
        // collections map to null so the client distinguishes "no prefill" cleanly.
        var prefill = prefillResolver.ResolvePrefill(resolved.Schema, resolved.SubtreeRoot, conversation);

        return Results.Ok(new TypificationFormResponse(
            Schema: TypificationEndpoints.ToSchemaDto(resolved.Schema),
            SubtreeRootNodeId: resolved.SubtreeRoot?.Value,
            PrefilledNodePath: prefill.PrefilledNodePath.Count > 0
                ? prefill.PrefilledNodePath.Select(n => n.Value).ToArray()
                : null,
            PrefilledFieldValues: prefill.PrefilledFieldValues.Count > 0
                ? prefill.PrefilledFieldValues
                : null));
    }

    // D1 — AI auto-disposition suggestion (P2a, SuggestOnly). Best-effort: every
    // "can't suggest" path returns 200 with an all-null TypificationSuggestionResponse
    // (no schema / AI disabled / classifier degraded / below confidence / sentiment-gated)
    // so the client treats it uniformly as "no suggestion, fall back to manual". The route
    // is license-gated (AdvancedTypification + TypificationAi) at registration.
    private static async Task<IResult> GetTypificationSuggestion(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] ITypificationResolver resolver,
        [FromServices] ITypificationAiClassifier classifier,
        [FromServices] IMessageStore messageStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var conversationId = EntityId.From(id);

        // 1. Conversation must exist.
        var conversation = await conversationStore.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return Results.NotFound();

        // 1b. No bound schema → nothing to classify against.
        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return Results.Ok(EmptySuggestion);

        // 2. AI disabled for this schema → no suggestion.
        if (!resolved.Schema.AiConfig.Enabled)
            return Results.Ok(EmptySuggestion);

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

        // 4. Classify (never throws — degrades to null on no-text / LLM-failure / invalid).
        var classification = await classifier.ClassifyAsync(resolved.Schema, resolved.SubtreeRoot, conversation, transcript, ct);
        if (classification is null)
            return Results.Ok(EmptySuggestion);

        var aiConfig = resolved.Schema.AiConfig;

        // 5a. Confidence gate.
        if (classification.Confidence < aiConfig.ConfidenceThreshold)
            return Results.Ok(EmptySuggestion);

        // 5b. Sentiment gate: never surface a Success outcome on a very-negative call.
        if (aiConfig.SentimentGating &&
            string.Equals(classification.Sentiment, "very_negative", StringComparison.OrdinalIgnoreCase) &&
            classification.NodePath.Count > 0)
        {
            var leafNodeId = classification.NodePath[^1];
            var leaf = resolved.Schema.Nodes.FirstOrDefault(n => n.NodeId == leafNodeId)?.Leaf;
            if (leaf?.Category == TypificationCategory.Success)
                return Results.Ok(EmptySuggestion);
        }

        // 5c. AiMode: P2a ships SuggestOnly semantics — always return as a suggestion (the
        // client decides whether to apply). AutoApplyAboveThreshold is a P2b distinction.
        return Results.Ok(new TypificationSuggestionResponse(
            SuggestedNodePath: classification.NodePath.Select(n => n.Value).ToArray(),
            SuggestedFieldValues: classification.FieldValues.Count > 0 ? classification.FieldValues : null,
            Confidence: classification.Confidence,
            Sentiment: classification.Sentiment));
    }

    /// <summary>All-null "no suggestion" payload reused by every degrade path.</summary>
    private static readonly TypificationSuggestionResponse EmptySuggestion =
        new(SuggestedNodePath: null, SuggestedFieldValues: null, Confidence: null, Sentiment: null);

    private static async Task<IResult> TypifyConversation(
        string id,
        HttpContext context,
        [FromServices] IConversationStore conversationStore,
        [FromServices] ITypificationResolver resolver,
        [FromServices] ITypificationValidator validator,
        [FromServices] ITypificationSubmissionStore submissionStore,
        CampaignStoreBase campaignStore,
        DispositionCodeStoreBase dispositionCodeStore,
        PlatformEventBus eventBus,
        IClock clock,
        [FromBody] TypifyRequest body,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var conversationId = EntityId.From(id);

        // 1. Load conversation + resolve the bound published schema.
        var conversation = await conversationStore.GetByIdAsync(tenantId, conversationId, ct);
        if (conversation is null)
            return Results.NotFound();

        var resolved = await resolver.ResolveForConversationAsync(conversation, ct);
        if (resolved is null)
            return Results.BadRequest(new ErrorResponse("no typification schema bound"));

        var schema = resolved.Schema;

        // 2. Map the selected node-path strings → EntityId list.
        var path = body.SelectedNodePath.Select(EntityId.From).ToList();

        // 3. Server-authoritative validation of the submission FIRST — a validation
        //    failure must NOT mutate or persist any conversation state.
        var validation = validator.ValidateSubmission(schema, path, body.FieldValues);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new TypifyErrorResponse(
                validation.Errors.Select(e => new TypifyFieldError(e.Field, e.Message)).ToArray()));
        }

        // 4. Move to WrapUp (same state-machine guard the old /wrapup handler used)
        //    only after validation succeeds, then persist.
        try
        {
            conversation.TransitionTo(ConversationState.WrapUp);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        await conversationStore.SaveAsync(conversation, ct);

        var leafNodeId = path[^1];

        // 4. Persist the submission.
        var submission = new TypificationSubmission
        {
            TenantId = tenantId,
            ConversationId = conversationId,
            AgentId = agentId,
            SchemaId = schema.SchemaId,
            SchemaVersion = schema.Version,
            SelectedNodePath = path,
            LeafNodeId = leafNodeId,
            FieldValues = body.FieldValues,
            Notes = body.Notes,
            AiSuggested = body.AiSuggested ?? false,
            AiConfidence = body.AiConfidence,
            AiAccepted = body.AiAccepted,
            Source = body.AiSuggested == true ? SubmissionSource.AutoAi : SubmissionSource.Manual,
            Duration = TimeSpan.Zero,
            CompletedAt = clock.UtcNow,
        };

        await submissionStore.SaveAsync(submission, ct);

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
                        tenantStr, campaignId, contactId, scheduledAt, agentSubId, ct);
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

        return Results.Ok(submission);
    }

    private static async Task<IResult> CreateConversation(
        HttpContext context,
        [FromBody] CreateConversationRequest body,
        IConversationService conversationService,
        [FromServices] IContactStore contactStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);

        if (!Enum.TryParse<ChannelType>(body.Channel, ignoreCase: true, out var channelType))
            return Results.BadRequest(new ErrorResponse($"Unknown channel: {body.Channel}"));

        var contactId = EntityId.From(body.ContactId);

        var contact = await contactStore.GetByIdAsync(tenantId, contactId, ct);
        if (contact is null)
            return Results.NotFound(new ErrorResponse("Contact not found"));

        var conversation = await conversationService.GetOrCreateForContactAsync(
            tenantId, contactId, channelType, ct);

        if (body.InitialMessage is not null)
        {
            var envelope = new MessageEnvelope([new TextBlock(body.InitialMessage)]);
            await conversationService.SendMessageAsync(
                conversation.ConversationId, tenantId, envelope,
                agentId, ConversationOwnerKind.Agent, ct);
        }

        return Results.Created($"/conversations/{conversation.ConversationId.Value}", conversation);
    }

    private static async Task<IResult> HoldConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.HoldAsync(EntityId.From(id), tenantId, agentId, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(new ErrorResponse(result.FailureReason ?? "Cannot hold conversation"));
    }

    private static async Task<IResult> UnholdConversation(
        string id,
        HttpContext context,
        IConversationSwitchboard switchboard,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agentId = GetCurrentAgentId(context);
        var result = await switchboard.UnholdAsync(EntityId.From(id), tenantId, agentId, ct);

        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(new ErrorResponse(result.FailureReason ?? "Cannot unhold conversation"));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    private static EntityId GetCurrentAgentId(HttpContext context)
    {
        // Same `sub`-first ordering as AgentEndpoints.GetCurrentUserId — the JWT
        // emitted by JwtTokenService carries the user id in `sub`, and
        // MapInboundClaims=false on the JwtBearerOptions means it is NOT
        // auto-remapped to NameIdentifier. Without `sub` first, every
        // /conversations/{id}/* call from a JWT-authenticated agent gets a
        // fresh random EntityId and downstream Switchboard guards (e.g.
        // AcceptAsync's "only the assigned agent can accept") reject the call.
        var nameId = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
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
/// D1 — AI auto-disposition suggestion (P2a). The suggested root→leaf node-id path,
/// optional captured field values, the model's confidence, and a free-form sentiment
/// hint. All members are <see langword="null"/> (the all-null payload) when there is no
/// suggestion — no bound schema, AI disabled, the classifier degraded, the confidence
/// fell below the schema threshold, or a sentiment-gated Success outcome was suppressed.
/// </summary>
internal sealed record TypificationSuggestionResponse(
    IReadOnlyList<string>? SuggestedNodePath,
    IReadOnlyDictionary<string, string>? SuggestedFieldValues,
    double? Confidence,
    string? Sentiment);

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

internal sealed record CreateConversationRequest(
    string ContactId,
    string Channel,
    string? InitialMessage = null);
