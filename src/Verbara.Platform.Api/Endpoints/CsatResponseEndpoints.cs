using Verbara.Platform.Api.Dtos;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// CSAT response capture + analytics endpoints (csat-runner Phase B). Persists each
/// capture through the brownfield-extended <see cref="ISurveyResponseStore"/>
/// (Platform/ADR-0020), publishes a <see cref="CsatResponseRecordedEvent"/> to the
/// realtime bus, and writes a <c>csat</c>-category audit row. The whole group is gated
/// by <see cref="LicenseFeature.CsatRunner"/> — the shared <c>LicenseGateMiddleware</c>
/// returns HTTP 402 + RFC 9457 ProblemDetails when the feature is unlicensed.
/// </summary>
internal static class CsatResponseEndpoints
{
    private const string ServiceKeyHeader = "X-Service-Key";

    public static void MapCsatResponseEndpoints(this IEndpointRouteBuilder app, string? internalServiceKey)
    {
        // Public webchat capture — anonymous but session-token-verified. License-gated.
        var webchat = app.MapGroup("/csat/responses")
            .AllowAnonymous()
            .RequireLicenseFeature(LicenseFeature.CsatRunner);

        webchat.MapPost("/webchat", CaptureWebChat);

        // Public voice capture (csat-completion, Platform/ADR-0020) — the survey-IVR leg submits the
        // collected DTMF rating here after the handoff. Anonymous but voice-token-verified. License-gated.
        var voice = app.MapGroup("/csat/responses")
            .AllowAnonymous()
            .RequireLicenseFeature(LicenseFeature.CsatRunner);

        voice.MapPost("/voice", CaptureVoice);

        // Internal email/sms capture — worker service-key gated (the IMAP poller /
        // SMS correlator forward parsed digit replies here). License-gated.
        var internalCapture = app.MapGroup("/csat/responses")
            .AllowAnonymous()
            .RequireLicenseFeature(LicenseFeature.CsatRunner)
            .AddEndpointFilter(async (ctx, next) =>
            {
                if (string.IsNullOrEmpty(internalServiceKey))
                    return Results.Problem(
                        title: "Service key not configured",
                        detail: "Services:ServiceKey is required to enable internal CSAT capture.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                var provided = ctx.HttpContext.Request.Headers[ServiceKeyHeader].FirstOrDefault();
                if (!string.Equals(provided, internalServiceKey, StringComparison.Ordinal))
                    return Results.Unauthorized();

                return await next(ctx).ConfigureAwait(false);
            });

        internalCapture.MapPost("/email", CaptureEmail);
        internalCapture.MapPost("/sms", CaptureSms);

        // CSAT analytics reads — SupervisorPlus + license-gated. Per-queue drill-down + the scope-wide
        // aggregate roll-up (csat-completion, Platform/ADR-0020).
        var analytics = app.MapGroup("/analytics/csat")
            .RequireAuthorization("SupervisorPlus")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.CsatRunner);

        analytics.MapGet("/queues/{queueId}", GetQueueCsatSummary);
        analytics.MapGet("/", GetScopeCsatAggregate);
    }

    // ─── WebChat (public, token-verified) ───────────────────────────────────────

    private static async Task<Results<Ok<CsatResponseDto>, UnauthorizedHttpResult, ValidationProblem, BadRequest>> CaptureWebChat(
        [FromBody] CsatResponseRequest body,
        HttpContext context,
        [FromServices] ICsatWebChatTokenVerifier tokenVerifier,
        [FromServices] ISurveyResponseStore responseStore,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var token = tokenVerifier.Verify(body.ResponseToken, DateTimeOffset.UtcNow);

        // Missing / malformed / expired token → reject, persist nothing.
        if (token is null)
            return TypedResults.Unauthorized();

        // The signed tenant/queue/channel MUST match the submitted claims.
        if (!string.Equals(token.QueueName, body.QueueName, StringComparison.Ordinal) ||
            !string.Equals(token.Channel, body.Channel, StringComparison.Ordinal) ||
            !string.Equals(body.Channel, "webchat", StringComparison.Ordinal))
            return TypedResults.Unauthorized();

        var validation = ValidateBody(body);
        if (validation is not null)
            return validation;

        var tenantId = new TenantId(token.TenantId);
        var result = await CaptureAsync(body, tenantId, context, responseStore, eventBus, audit, ct).ConfigureAwait(false);
        return result.Result switch
        {
            Ok<CsatResponseDto> ok => ok,
            ValidationProblem vp => vp,
            _ => TypedResults.BadRequest(),
        };
    }

    // ─── Voice (public, voice-token-verified) — csat-completion ─────────────────

    private static async Task<Results<Ok<CsatResponseDto>, UnauthorizedHttpResult, ValidationProblem, BadRequest>> CaptureVoice(
        [FromBody] CsatResponseRequest body,
        HttpContext context,
        [FromServices] ICsatVoiceTokenVerifier tokenVerifier,
        [FromServices] ISurveyResponseStore responseStore,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var token = tokenVerifier.Verify(body.ResponseToken, DateTimeOffset.UtcNow);

        // Missing / malformed / expired token → reject, persist nothing.
        if (token is null)
            return TypedResults.Unauthorized();

        // The signed tenant/queue/channel MUST match the submitted claims; voice tokens only sign voice.
        if (!string.Equals(token.QueueName, body.QueueName, StringComparison.Ordinal) ||
            !string.Equals(token.Channel, body.Channel, StringComparison.Ordinal) ||
            !string.Equals(body.Channel, "voice", StringComparison.Ordinal))
            return TypedResults.Unauthorized();

        var validation = ValidateBody(body);
        if (validation is not null)
            return validation;

        var tenantId = new TenantId(token.TenantId);
        // Voice DTMF carries no free text — the capture's comment is null (frozen fixture). CallId is set
        // from the correlated voice conversation so the CDR can join the recorded rating to the call.
        var result = await CaptureAsync(body, tenantId, context, responseStore, eventBus, audit, ct, setCallId: true).ConfigureAwait(false);
        return result.Result switch
        {
            Ok<CsatResponseDto> ok => ok,
            ValidationProblem vp => vp,
            _ => TypedResults.BadRequest(),
        };
    }

    // ─── Email (internal, service-key gated) ────────────────────────────────────

    private static async Task<Results<Ok<CsatResponseDto>, ValidationProblem, BadRequest>> CaptureEmail(
        [FromBody] CsatResponseRequest body,
        HttpContext context,
        [FromServices] ISurveyResponseStore responseStore,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IAuditService audit,
        CancellationToken ct)
        => await CaptureInternalAsync(body, "email", context, responseStore, eventBus, audit, ct).ConfigureAwait(false);

    // ─── SMS (internal, service-key gated) ──────────────────────────────────────

    private static async Task<Results<Ok<CsatResponseDto>, ValidationProblem, BadRequest>> CaptureSms(
        [FromBody] CsatResponseRequest body,
        HttpContext context,
        [FromServices] ISurveyResponseStore responseStore,
        [FromServices] PlatformEventBus eventBus,
        [FromServices] IAuditService audit,
        CancellationToken ct)
        => await CaptureInternalAsync(body, "sms", context, responseStore, eventBus, audit, ct).ConfigureAwait(false);

    private static async Task<Results<Ok<CsatResponseDto>, ValidationProblem, BadRequest>> CaptureInternalAsync(
        CsatResponseRequest body,
        string expectedChannel,
        HttpContext context,
        ISurveyResponseStore responseStore,
        PlatformEventBus eventBus,
        IAuditService audit,
        CancellationToken ct)
    {
        if (!string.Equals(body.Channel, expectedChannel, StringComparison.Ordinal))
            return TypedResults.BadRequest();

        // Internal callers must carry a tenant on the request path (worker context sets X-Tenant-Id).
        if (!context.Items.TryGetValue("TenantId", out var val) || val is not TenantId tenantId)
            return TypedResults.BadRequest();

        var validation = ValidateBody(body);
        if (validation is not null)
            return validation;

        var result = await CaptureAsync(body, tenantId, context, responseStore, eventBus, audit, ct).ConfigureAwait(false);
        return result.Result switch
        {
            Ok<CsatResponseDto> ok => ok,
            ValidationProblem vp => vp,
            _ => TypedResults.BadRequest(),
        };
    }

    // ─── Shared capture (persist + publish + audit) ─────────────────────────────

    private static async Task<Results<Ok<CsatResponseDto>, ValidationProblem, BadRequest>> CaptureAsync(
        CsatResponseRequest body,
        TenantId tenantId,
        HttpContext context,
        ISurveyResponseStore responseStore,
        PlatformEventBus eventBus,
        IAuditService audit,
        CancellationToken ct,
        bool setCallId = false)
    {
        if (!EntityId.IsValid(body.SurveyId) || !EntityId.IsValid(body.ConversationId))
            return TypedResults.BadRequest();

        var responseId = EntityId.New();
        var conversationId = EntityId.From(body.ConversationId);

        var response = new SurveyResponse
        {
            ResponseId = responseId,
            SurveyId = EntityId.From(body.SurveyId),
            TenantId = tenantId,
            ConversationId = conversationId,
            ContactId = conversationId, // capture has no distinct contact id; correlate by conversation
            // Voice captures (csat-completion) set CallId from the correlated voice conversation so the
            // CDR can join the recorded rating to the call; digital captures leave it null.
            CallId = setCallId ? conversationId : null,
            Answers = [new SurveyAnswer(EntityId.From(SurveyQuestionIds.CsatRating), body.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            SubmittedAt = body.CapturedAt,
            Channel = body.Channel,
            QueueName = body.QueueName,
            Rating = body.Rating,
            Comment = body.Comment,
            CapturedAt = body.CapturedAt,
        };

        await responseStore.SaveAsync(response, ct).ConfigureAwait(false);

        eventBus.Publish(new CsatResponseRecordedEvent(
            TenantId: tenantId.Value,
            ResponseId: responseId.Value,
            SurveyId: body.SurveyId,
            ConversationId: body.ConversationId,
            Channel: body.Channel,
            QueueName: body.QueueName,
            Rating: body.Rating,
            Comment: body.Comment,
            CapturedAt: body.CapturedAt));

        await audit.RecordAsync(
            tenantId,
            category: "csat",
            action: "csat.response.recorded",
            severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system",
            actorType: "user",
            targetId: responseId.Value,
            targetType: "survey_response",
            metadata: new Dictionary<string, string>
            {
                ["channel"] = body.Channel,
                ["queue"] = body.QueueName,
                ["rating"] = body.Rating.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["conversationId"] = body.ConversationId,
            },
            ct: ct).ConfigureAwait(false);

        return TypedResults.Ok(new CsatResponseDto(
            QueueName: body.QueueName,
            Channel: body.Channel,
            TotalResponses: 1,
            AverageRating: body.Rating,
            RangeStart: body.CapturedAt,
            RangeEnd: body.CapturedAt));
    }

    // ─── Analytics read (SupervisorPlus) ────────────────────────────────────────

    private static async Task<Results<Ok<CsatResponseDto>, BadRequest>> GetQueueCsatSummary(
        string queueId,
        HttpContext context,
        [FromServices] ISurveyAnalytics analytics,
        [FromQuery] string? range,
        [FromQuery] string? channel,
        CancellationToken ct)
    {
        if (!context.Items.TryGetValue("TenantId", out var val) || val is not TenantId tenantId)
            return TypedResults.BadRequest();

        if (string.IsNullOrWhiteSpace(queueId))
            return TypedResults.BadRequest();

        var end = DateTimeOffset.UtcNow;
        var start = end - ParseRange(range);
        var dateRange = new DateRange(start, end);
        var channelFilter = string.IsNullOrWhiteSpace(channel) ? "webchat" : channel;

        var summary = await analytics
            .GetByQueueAndChannelAsync(tenantId, queueId, channelFilter, dateRange, ct)
            .ConfigureAwait(false);

        return TypedResults.Ok(new CsatResponseDto(
            QueueName: queueId,
            Channel: channelFilter,
            TotalResponses: summary.TotalResponses,
            AverageRating: summary.AverageScore,
            RangeStart: start,
            RangeEnd: end));
    }

    // ─── Scope-wide aggregate read (csat-completion) ────────────────────────────

    private static async Task<Results<Ok<CsatAggregateDto>, BadRequest>> GetScopeCsatAggregate(
        HttpContext context,
        [FromServices] ISurveyAnalytics analytics,
        [FromQuery] string? range,
        [FromQuery] string? channel,
        CancellationToken ct)
    {
        if (!context.Items.TryGetValue("TenantId", out var val) || val is not TenantId tenantId)
            return TypedResults.BadRequest();

        var end = DateTimeOffset.UtcNow;
        var start = end - ParseRange(range);
        var dateRange = new DateRange(start, end);
        // The channel filter echoes back into the envelope + every queues[] row; 'all' when unfiltered.
        var hasChannel = !string.IsNullOrWhiteSpace(channel);
        var channelEcho = hasChannel ? channel! : "all";

        var scope = await analytics
            .GetScopeAggregateAsync(tenantId, hasChannel ? channel : null, dateRange, ct)
            .ConfigureAwait(false);

        var queues = scope.Queues
            .Select(q => new CsatResponseDto(
                QueueName: q.QueueName,
                Channel: channelEcho,
                TotalResponses: q.TotalResponses,
                AverageRating: q.AverageRating,
                RangeStart: start,
                RangeEnd: end))
            .ToList();

        return TypedResults.Ok(new CsatAggregateDto(
            TotalResponses: scope.TotalResponses,
            AverageRating: scope.AverageRating,
            RangeStart: start,
            RangeEnd: end,
            Queues: queues));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static ValidationProblem? ValidateBody(CsatResponseRequest body)
    {
        var errors = new Dictionary<string, string[]>();

        if (body.Rating is < 1 or > 5)
            errors["rating"] = ["Rating must be between 1 and 5."];

        if (!string.Equals(body.QuestionId, SurveyQuestionIds.CsatRating, StringComparison.Ordinal))
            errors["questionId"] = [$"questionId must be '{SurveyQuestionIds.CsatRating}'."];

        if (string.IsNullOrWhiteSpace(body.QueueName))
            errors["queueName"] = ["queueName is required."];

        return errors.Count > 0 ? TypedResults.ValidationProblem(errors) : null;
    }

    private static TimeSpan ParseRange(string? range) => range switch
    {
        "1h" => TimeSpan.FromHours(1),
        "24h" or null or "" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromHours(24),
    };
}
