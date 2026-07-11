using Verbara.Platform.Api.Dtos;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Admin CRUD surface for the per-tenant CSAT prompt template store (csat-runner Phase E),
/// under <c>/api/v1/admin/csat/templates/*</c>. The whole group is <c>AdminOnly</c> and
/// operational-tenant scoped; every mutation writes a <c>csat</c>-category audit row via
/// <see cref="IAuditService"/>. The store this manages backs the in-process
/// <c>ICsatTemplateProvider</c> the Pro engine resolves prompts through.
/// </summary>
/// <remarks>
/// <c>POST …/{id}/preview-voice</c> is present in shape but returns HTTP 501: Pro's voice
/// channel adapter (and its TTS synthesis) is <b>deferred</b> — the Pro CSAT nupkg ships no
/// <c>ITtsSynthesizer</c> and <c>CsatVoiceOptions</c> is intentionally absent (digital-first;
/// voice is a Path-A follow-up per Pro <c>docs/research/2026-07-07-csat-voice-bridge-ownership.md</c>).
/// Rather than fake a synthesis, the endpoint returns a clear "voice preview deferred" RFC 9457
/// ProblemDetails so callers get an honest, forward-compatible contract.
/// </remarks>
internal static class CsatTemplateAdminEndpoints
{
    public static void MapCsatTemplateAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin/csat/templates")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        admin.MapGet("/", ListTemplates);
        admin.MapGet("/{id}", GetTemplate);
        admin.MapPut("/{id}", UpsertTemplate);
        admin.MapDelete("/{id}", DeleteTemplate);
        admin.MapPost("/{id}/preview-voice", PreviewVoice);
    }

    // ─── List ────────────────────────────────────────────────────────────────

    private static async Task<Ok<CsatTemplateDto[]>> ListTemplates(
        HttpContext context,
        [FromServices] ICsatTemplateStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var templates = await store.GetAllAsync(tenantId, ct).ConfigureAwait(false);
        return TypedResults.Ok(templates.Select(ToDto).ToArray());
    }

    // ─── Get ─────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<CsatTemplateDto>, NotFound, BadRequest>> GetTemplate(
        string id,
        HttpContext context,
        [FromServices] ICsatTemplateStore store,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return TypedResults.BadRequest();

        var tenantId = GetTenantId(context);
        var template = await store.GetByIdAsync(tenantId, EntityId.From(id), ct).ConfigureAwait(false);
        return template is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(template));
    }

    // ─── Upsert (PUT) ──────────────────────────────────────────────────────────

    private static async Task<Results<Ok<CsatTemplateDto>, ValidationProblem, BadRequest>> UpsertTemplate(
        string id,
        [FromBody] UpsertCsatTemplateRequest body,
        HttpContext context,
        [FromServices] ICsatTemplateStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return TypedResults.BadRequest();

        var validation = Validate(body);
        if (validation is not null)
            return validation;

        var tenantId = GetTenantId(context);
        var templateId = EntityId.From(id);
        var existing = await store.GetByIdAsync(tenantId, templateId, ct).ConfigureAwait(false);

        var entry = new CsatTemplateEntry
        {
            TemplateId = templateId,
            TenantId = tenantId,
            Channel = body.Channel,
            Locale = body.Locale,
            Subject = body.Subject,
            Body = body.Body,
            IsDefault = body.IsDefault ?? existing?.IsDefault ?? false,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = existing is null ? null : DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(entry, ct).ConfigureAwait(false);

        await audit.RecordAsync(
            tenantId,
            category: "csat",
            action: existing is null ? "csat.template.created" : "csat.template.updated",
            severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system",
            actorType: "user",
            targetId: id,
            targetType: "csat_template",
            changes: new AuditChanges(
                Before: existing is null ? null : new { existing.Channel, existing.Locale, existing.IsDefault },
                After: new { entry.Channel, entry.Locale, entry.IsDefault }),
            metadata: new Dictionary<string, string>
            {
                ["channel"] = entry.Channel,
                ["locale"] = entry.Locale,
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct).ConfigureAwait(false);

        return TypedResults.Ok(ToDto(entry));
    }

    // ─── Delete ────────────────────────────────────────────────────────────────

    private static async Task<Results<NoContent, NotFound, BadRequest>> DeleteTemplate(
        string id,
        HttpContext context,
        [FromServices] ICsatTemplateStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        if (!EntityId.IsValid(id))
            return TypedResults.BadRequest();

        var tenantId = GetTenantId(context);
        var templateId = EntityId.From(id);
        var existing = await store.GetByIdAsync(tenantId, templateId, ct).ConfigureAwait(false);
        if (existing is null)
            return TypedResults.NotFound();

        await store.DeleteAsync(tenantId, templateId, ct).ConfigureAwait(false);

        await audit.RecordAsync(
            tenantId,
            category: "csat",
            action: "csat.template.deleted",
            severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system",
            actorType: "user",
            targetId: id,
            targetType: "csat_template",
            changes: new AuditChanges(
                Before: new { existing.Channel, existing.Locale, existing.IsDefault },
                After: null),
            metadata: new Dictionary<string, string>
            {
                ["channel"] = existing.Channel,
                ["locale"] = existing.Locale,
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    // ─── Preview voice (deferred) ────────────────────────────────────────────────

    private static async Task<Results<NotFound, BadRequest, ProblemHttpResult>> PreviewVoice(
        string id,
        HttpContext context,
        [FromServices] ICsatTemplateStore store,
        CancellationToken ct)
    {
        // Endpoint shape is present, but Pro's voice channel adapter + TTS synthesis are
        // DEFERRED (digital-first; the Pro CSAT nupkg ships no ITtsSynthesizer and
        // CsatVoiceOptions is intentionally absent). We resolve the template so a missing id
        // still 404s honestly, then return HTTP 501 rather than fabricate audio.
        if (!EntityId.IsValid(id))
            return TypedResults.BadRequest();

        var tenantId = GetTenantId(context);
        var template = await store.GetByIdAsync(tenantId, EntityId.From(id), ct).ConfigureAwait(false);
        if (template is null)
            return TypedResults.NotFound();

        return TypedResults.Problem(
            title: "Voice preview deferred",
            detail: "Voice CSAT preview is not yet available: the Pro voice channel adapter and TTS " +
                    "synthesis are deferred (digital-first). This endpoint will synthesize the template " +
                    "body once the Pro voice bridge ships.",
            statusCode: StatusCodes.Status501NotImplemented,
            type: "https://verbara.platform/errors/voice-preview-deferred");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static CsatTemplateDto ToDto(CsatTemplateEntry entry) =>
        new(
            TemplateId: entry.TemplateId.Value,
            Channel: entry.Channel,
            Locale: entry.Locale,
            Subject: entry.Subject,
            Body: entry.Body);

    private static readonly string[] s_validChannels = ["voice", "email", "sms"];

    private static ValidationProblem? Validate(UpsertCsatTemplateRequest body)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(body.Channel) || !s_validChannels.Contains(body.Channel))
            errors["channel"] = ["Channel must be one of: voice, email, sms."];

        if (string.IsNullOrWhiteSpace(body.Locale))
            errors["locale"] = ["Locale is required (BCP-47, e.g. en-US)."];

        if (string.IsNullOrWhiteSpace(body.Body))
            errors["body"] = ["Body is required."];

        return errors.Count > 0 ? (ValidationProblem)TypedResults.ValidationProblem(errors) : null;
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
