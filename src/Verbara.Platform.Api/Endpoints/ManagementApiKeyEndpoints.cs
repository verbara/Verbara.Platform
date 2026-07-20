using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class ManagementApiKeyEndpoints
{
    public static void MapManagementApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/api-keys").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", ListKeys);
        group.MapPost("/", CreateKey);
        group.MapPost("/{id}/rotate", RotateKey);
        group.MapDelete("/{id}", RevokeKey);
    }

    private static async Task<Ok<List<MgmtApiKeyDto>>> ListKeys(
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return TypedResults.Ok(new List<MgmtApiKeyDto>());

        var tenantId = new TenantId(host.TenantId);
        var keys = await apiKeyStore.ListAsync(tenantId, new PagedQuery(1, 100), ct);

        var mgmtKeys = keys.Items
            .Where(k => k.KeyType == ApiKeyType.Management)
            .Select(k => new MgmtApiKeyDto(
                k.KeyId.Value, k.Name, k.IsRevoked,
                k.ExpiresAt, k.CreatedAt, k.LastUsedAt))
            .ToList();

        return TypedResults.Ok(mgmtKeys);
    }

    private static async Task<IResult> CreateKey(
        HttpContext context,
        [FromBody] CreateMgmtApiKeyRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var rawKey = SecretTokenGenerator.Mint("mgmt_");
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

        var apiKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = new TenantId(host.TenantId),
            Name = body.Name ?? "Management Key",
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            ExpiresAt = body.ExpiresInDays.HasValue
                ? clock.UtcNow.AddDays(body.ExpiresInDays.Value)
                : null,
            CreatedAt = clock.UtcNow,
        };

        await apiKeyStore.SaveAsync(apiKey, ct);
        await audit.RecordAsync(
            new TenantId(host.TenantId), category: "admin", action: "api_key.created", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: apiKey.KeyId.Value, targetType: "api_key",
            changes: new AuditChanges(Before: null, After: new { apiKey.KeyId, apiKey.Name, apiKey.ExpiresAt }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/management/api-keys/{apiKey.KeyId.Value}",
            new CreateMgmtApiKeyResponse(apiKey.KeyId.Value, apiKey.Name, rawKey, apiKey.ExpiresAt));
    }

    private static async Task<Results<Ok<CreateMgmtApiKeyResponse>, ProblemHttpResult, NotFound>> RotateKey(
        string id,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return TypedResults.Problem("Platform not initialized.", statusCode: 503);

        var tenantId = new TenantId(host.TenantId);
        var existing = await apiKeyStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null || existing.KeyType != ApiKeyType.Management)
            return TypedResults.NotFound();

        // Revoke old key
        await apiKeyStore.RevokeAsync(tenantId, existing.KeyId, ct);

        // Create new key with same name
        var rawKey = SecretTokenGenerator.Mint("mgmt_");
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

        var newKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = tenantId,
            Name = existing.Name,
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            ExpiresAt = existing.ExpiresAt,
            CreatedAt = clock.UtcNow,
        };

        await apiKeyStore.SaveAsync(newKey, ct);
        await audit.RecordAsync(
            tenantId, category: "admin", action: "api_key.rotated", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: newKey.KeyId.Value, targetType: "api_key",
            changes: new AuditChanges(Before: new { KeyId = id, existing.Name }, After: new { newKey.KeyId, newKey.Name }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
                ["previous_key_id"] = id,
            },
            ct: ct);
        return TypedResults.Ok(new CreateMgmtApiKeyResponse(newKey.KeyId.Value, newKey.Name, rawKey, newKey.ExpiresAt));
    }

    private static async Task<IResult> RevokeKey(
        string id,
        HttpContext context,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var tenantId = new TenantId(host.TenantId);
        var existing = await apiKeyStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null || existing.KeyType != ApiKeyType.Management)
            return Results.NotFound();

        await apiKeyStore.RevokeAsync(tenantId, existing.KeyId, ct);
        await audit.RecordAsync(
            tenantId, category: "admin", action: "api_key.revoked", severity: "warning",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id, targetType: "api_key",
            changes: new AuditChanges(Before: new { existing.KeyId, existing.Name, IsRevoked = false }, After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtApiKeyDto(
    string KeyId,
    string Name,
    bool IsRevoked,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    // R5.2 PC.5 / B.12 — populated by the auth middleware via
    // IApiKeyLastUsedStamper (debounced ≤ 1 write/min/key). Null when the
    // key has never authenticated successfully.
    DateTimeOffset? LastUsedAt);

internal sealed record CreateMgmtApiKeyRequest(
    string? Name = null,
    int? ExpiresInDays = null);

internal sealed record CreateMgmtApiKeyResponse(
    string KeyId,
    string Name,
    string ApiKey,
    DateTimeOffset? ExpiresAt);
