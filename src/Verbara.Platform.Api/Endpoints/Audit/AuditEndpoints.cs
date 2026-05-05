using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Audit;

/// <summary>
/// R5.2 PB.1 — admin audit log viewer endpoints.
/// </summary>
/// <remarks>
/// Two surfaces gated behind the new <c>audit.read</c> / <c>audit.export</c>
/// permissions seeded by P0.9 (commit <c>f20892e</c>):
/// <list type="bullet">
///   <item><description><c>GET /admin/audit/events</c> — paginated query with rich filters.</description></item>
///   <item><description><c>GET /admin/audit/export</c> — streaming CSV / JSON export of the same filter shape.</description></item>
/// </list>
/// Both endpoints scope to the caller's tenant unless an explicit
/// <c>tenantId</c> override is provided AND the caller passes the
/// PlatformAdmin gate (handled by the <c>AuditAdminGate</c> /
/// <c>AuditAdminExportGate</c> policies wired in <c>Program.cs</c>).
///
/// Wired in <c>Program.cs</c> via:
/// <code>
/// options.AddPolicy(AuditAdminEndpoints.QueryPolicy,
///     p => p.AddRequirements(new PlatformAdminRequirement("audit.read")));
/// options.AddPolicy(AuditAdminEndpoints.ExportPolicy,
///     p => p.AddRequirements(new PlatformAdminRequirement("audit.export")));
/// </code>
///
/// The legacy <c>/admin/audit</c> endpoints in <see cref="AuditEndpoints"/>
/// (root namespace) remain wired with the <c>AdminOnly</c> policy for
/// back-compat with the original audit page.
/// </remarks>
internal static class AuditAdminEndpoints
{
    public const string QueryPolicy = "AuditAdminGate";
    public const string ExportPolicy = "AuditAdminExportGate";

    private const int DefaultRetentionDays = 90;
    public const string RetentionHeader = "X-Audit-Retention-Days";

    public static void MapAuditAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/audit");

        group.MapGet("/events", QueryEvents).RequireAuthorization(QueryPolicy);
        group.MapGet("/export", ExportEvents).RequireAuthorization(ExportPolicy);
    }

    // ─── Query (paginated) ───────────────────────────────────────────────────

    private static async Task<IResult> QueryEvents(
        HttpContext context,
        [FromServices] IAuditQueryService service,
        [FromServices] IFeatureGateService featureGate,
        string? actionPrefix = null,
        string? actor = null,
        string? target = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? tenantId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var (effectiveTenant, error) = ResolveTenantScope(context, tenantId);
        if (error is not null)
            return error;

        var filter = new AuditQueryFilter
        {
            ActionPrefix = NullIfEmpty(actionPrefix),
            Actor = NullIfEmpty(actor),
            Target = NullIfEmpty(target),
            From = from,
            To = to,
            TenantId = effectiveTenant.Value,
            Page = page > 0 ? page : 1,
            PageSize = ClampPageSize(pageSize),
        };

        var result = await service.QueryAsync(effectiveTenant, filter, ct);

        var retention = ResolveRetentionDays(featureGate, effectiveTenant.Value);
        context.Response.Headers[RetentionHeader] =
            retention.ToString(CultureInfo.InvariantCulture);

        return Results.Ok(result);
    }

    // ─── Export (streaming) ──────────────────────────────────────────────────

    private static async Task ExportEvents(
        HttpContext context,
        [FromServices] IAuditQueryService service,
        [FromServices] IFeatureGateService featureGate,
        string? format = "csv",
        string? actionPrefix = null,
        string? actor = null,
        string? target = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var (effectiveTenant, error) = ResolveTenantScope(context, tenantId);
        if (error is not null)
        {
            await error.ExecuteAsync(context);
            return;
        }

        var filter = new AuditQueryFilter
        {
            ActionPrefix = NullIfEmpty(actionPrefix),
            Actor = NullIfEmpty(actor),
            Target = NullIfEmpty(target),
            From = from,
            To = to,
            TenantId = effectiveTenant.Value,
        };

        var retention = ResolveRetentionDays(featureGate, effectiveTenant.Value);
        context.Response.Headers[RetentionHeader] =
            retention.ToString(CultureInfo.InvariantCulture);

        var fmt = (format ?? "csv").ToLowerInvariant();
        if (fmt == "json")
        {
            await StreamJsonAsync(context, service, effectiveTenant, filter, ct);
        }
        else
        {
            await StreamCsvAsync(context, service, effectiveTenant, filter, ct);
        }
    }

    private static async Task StreamCsvAsync(
        HttpContext context,
        IAuditQueryService service,
        TenantId tenantId,
        AuditQueryFilter filter,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"audit-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv\"";

        await using var writer = new StreamWriter(context.Response.Body, Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(
            "entryId,occurredAt,action,category,severity,actorId,actorType,targetId,targetType,impersonatorId,correlationId,status".AsMemory(),
            ct);

        await foreach (var dto in service.StreamAsync(tenantId, filter, ct).ConfigureAwait(false))
        {
            var line = string.Join(',', new[]
            {
                CsvEscape(dto.EntryId),
                CsvEscape(dto.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                CsvEscape(dto.Action),
                CsvEscape(dto.Category),
                CsvEscape(dto.Severity),
                CsvEscape(dto.ActorId),
                CsvEscape(dto.ActorType),
                CsvEscape(dto.TargetId ?? string.Empty),
                CsvEscape(dto.TargetType ?? string.Empty),
                CsvEscape(dto.ImpersonatorId ?? string.Empty),
                CsvEscape(dto.CorrelationId ?? string.Empty),
                CsvEscape(dto.Status),
            });
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }

        await writer.FlushAsync(ct);
    }

    private static async Task StreamJsonAsync(
        HttpContext context,
        IAuditQueryService service,
        TenantId tenantId,
        AuditQueryFilter filter,
        CancellationToken ct)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"audit-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json\"";

        await using var writer = new StreamWriter(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        await writer.WriteAsync("[".AsMemory(), ct);

        var first = true;
        await foreach (var dto in service.StreamAsync(tenantId, filter, ct).ConfigureAwait(false))
        {
            if (!first)
                await writer.WriteAsync(",\n".AsMemory(), ct);
            first = false;

            var json = JsonSerializer.Serialize(dto, AuditJsonContext.Default.AuditEventDto);
            await writer.WriteAsync(json.AsMemory(), ct);
        }

        await writer.WriteAsync("]".AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static (TenantId Tenant, IResult? Error) ResolveTenantScope(
        HttpContext context, string? requestedTenant)
    {
        var actorTenantClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(actorTenantClaim))
        {
            // No tenant claim — fall back to the X-Tenant-Id header / Items.
            if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
                return (tid, null);
            return (default, Results.Forbid());
        }

        // Cross-tenant query is permitted only for PlatformAdmin / SystemAdmin
        // roles (the policy already enforced the audit.read permission). If the
        // caller asks for a tenant that isn't theirs but they aren't an admin
        // role, refuse rather than silently scoping back to actor tenant.
        if (string.IsNullOrEmpty(requestedTenant))
            return (new TenantId(actorTenantClaim), null);

        if (string.Equals(requestedTenant, actorTenantClaim, StringComparison.Ordinal))
            return (new TenantId(actorTenantClaim), null);

        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value
            ?? context.User.FindFirst("role")?.Value;
        if (roleClaim is "Admin" or "SystemAdmin" or "PlatformAdmin")
            return (new TenantId(requestedTenant), null);

        return (default, Results.Forbid());
    }

    private static int ResolveRetentionDays(IFeatureGateService featureGate, string tenantId)
    {
        try
        {
            var days = featureGate.GetAuditRetentionDays(tenantId);
            return days > 0 ? days : DefaultRetentionDays;
        }
        catch
        {
            return DefaultRetentionDays;
        }
    }

    private static int ClampPageSize(int requested)
    {
        if (requested <= 0) return 50;
        if (requested > 500) return 500;
        return requested;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var needsQuotes = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);
        if (!needsQuotes)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AuditEventDto))]
[JsonSerializable(typeof(PagedResult<AuditEventDto>))]
internal partial class AuditJsonContext : JsonSerializerContext;
