using System.Security.Claims;
using System.Text.Json.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity.Auth.Jwt;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints.Security;

/// <summary>
/// R5.4 S5.9 — admin endpoints for JWT signing-key rotation. Closes C.1 of
/// post-R5.1 triage (v1.9.2 partial single-key impl).
/// </summary>
/// <remarks>
/// Two surfaces gated behind the new <c>security.jwt.rotate</c> permission
/// seeded by R5.4 (PermissionSeeder + RoleTemplateSeeder.platform_admin):
/// <list type="bullet">
///   <item><description><c>POST /management/security/jwt/rotate-key</c> — mints a new active key,
///         demotes the prior active to validation-only, emits an audit entry.</description></item>
///   <item><description><c>GET /management/security/jwt/keys</c> — lists every live validation
///         key (active + grace-window) for operator visibility.</description></item>
/// </list>
/// Wired in <c>Program.cs</c> via:
/// <code>
/// options.AddPolicy(JwtKeyEndpoints.AuthorizationPolicy,
///     p =&gt; p.AddRequirements(new PlatformAdminRequirement("security.jwt.rotate")));
/// </code>
/// </remarks>
internal static class JwtKeyEndpoints
{
    /// <summary>Authorization policy name for the JWT key rotation surface.</summary>
    public const string AuthorizationPolicy = "JwtKeyRotationGate";

    public static void MapJwtKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/security/jwt")
            .RequireAuthorization(AuthorizationPolicy);

        group.MapPost("/rotate-key", RotateKey);
        group.MapGet("/keys", ListKeys);
    }

    private static async Task<IResult> RotateKey(
        HttpContext context,
        [FromServices] IJwtKeyRotationService rotation,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var (tenantId, actorId) = ResolveActor(context);

        var previous = await rotation.GetValidationKeysAsync(ct);
        var previousActive = previous.FirstOrDefault(k => k.IsActive);

        var newKey = await rotation.RotateAsync(ct);

        await audit.RecordAsync(
            tenantId,
            category: "admin",
            action: "security.jwt.key_rotated",
            severity: "warning",
            actorId: actorId,
            actorType: "user",
            targetId: newKey.KeyId,
            targetType: "jwt-key",
            metadata: BuildMetadata(context, tenantId.Value, previousActive?.KeyId, newKey),
            ct: ct);

        return Results.Ok(new RotateKeyResponse(
            KeyId: newKey.KeyId,
            ActivatedAt: newKey.ActivatedAt,
            ExpiresAt: newKey.ExpiresAt));
    }

    private static async Task<IResult> ListKeys(
        [FromServices] IJwtKeyRotationService rotation,
        CancellationToken ct)
    {
        var keys = await rotation.GetValidationKeysAsync(ct);
        var dtos = keys
            .OrderByDescending(k => k.ActivatedAt)
            .Select(k => new JwtKeyDto(k.KeyId, k.ActivatedAt, k.ExpiresAt, k.IsActive))
            .ToArray();
        return Results.Ok(new JwtKeyListResponse(dtos));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (TenantId TenantId, string ActorId) ResolveActor(HttpContext context)
    {
        var tenantClaim = context.User.FindFirst("tenant_id")?.Value
            ?? context.User.FindFirst("tid")?.Value
            ?? "platform";
        var userClaim = context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value
            ?? "system";
        return (new TenantId(tenantClaim), userClaim);
    }

    private static Dictionary<string, string> BuildMetadata(
        HttpContext context,
        string actorTenantId,
        string? previousKeyId,
        JwtKeyEntry newKey)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actor_tenant_id"] = actorTenantId,
            ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ["endpoint"] = context.Request.Path.Value ?? "",
            ["new_key_id"] = newKey.KeyId,
            ["new_key_activated_at"] = newKey.ActivatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["new_key_expires_at"] = newKey.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(previousKeyId))
            dict["previous_key_id"] = previousKeyId;
        return dict;
    }
}

// DTOs (records — AOT-safe via JsonSerializerContext below).

internal sealed record RotateKeyResponse(string KeyId, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt);

internal sealed record JwtKeyDto(string KeyId, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt, bool IsActive);

internal sealed record JwtKeyListResponse(IReadOnlyList<JwtKeyDto> Keys);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RotateKeyResponse))]
[JsonSerializable(typeof(JwtKeyDto))]
[JsonSerializable(typeof(JwtKeyListResponse))]
internal sealed partial class JwtKeyEndpointsJsonContext : JsonSerializerContext;
