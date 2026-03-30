using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementApiKeyEndpoints
{
    public static void MapManagementApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/management/api-keys").RequireAuthorization("PlatformAdminOnly");

        group.MapGet("/", ListKeys);
        group.MapPost("/", CreateKey);
        group.MapPost("/{id}/rotate", RotateKey);
        group.MapDelete("/{id}", RevokeKey);
    }

    private static async Task<IResult> ListKeys(
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Ok(Array.Empty<object>());

        var tenantId = new TenantId(host.TenantId);
        var keys = await apiKeyStore.ListAsync(tenantId, new PagedQuery(1, 100), ct);

        var mgmtKeys = keys.Items
            .Where(k => k.KeyType == ApiKeyType.Management)
            .Select(k => new MgmtApiKeyDto(
                k.KeyId.Value, k.Name, k.IsRevoked,
                k.ExpiresAt, k.CreatedAt))
            .ToList();

        return Results.Ok(mgmtKeys);
    }

    private static async Task<IResult> CreateKey(
        [FromBody] CreateMgmtApiKeyRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var rawKey = $"mgmt_{Guid.NewGuid():N}";
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

        return Results.Created($"/api/management/api-keys/{apiKey.KeyId.Value}",
            new CreateMgmtApiKeyResponse(apiKey.KeyId.Value, apiKey.Name, rawKey, apiKey.ExpiresAt));
    }

    private static async Task<IResult> RotateKey(
        string id,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var host = await tenantStore.GetHostTenantAsync(ct);
        if (host is null)
            return Results.Problem("Platform not initialized.", statusCode: 503);

        var tenantId = new TenantId(host.TenantId);
        var existing = await apiKeyStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (existing is null || existing.KeyType != ApiKeyType.Management)
            return Results.NotFound();

        // Revoke old key
        await apiKeyStore.RevokeAsync(tenantId, existing.KeyId, ct);

        // Create new key with same name
        var rawKey = $"mgmt_{Guid.NewGuid():N}";
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

        return Results.Ok(new CreateMgmtApiKeyResponse(newKey.KeyId.Value, newKey.Name, rawKey, newKey.ExpiresAt));
    }

    private static async Task<IResult> RevokeKey(
        string id,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IApiKeyStore apiKeyStore,
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
        return Results.NoContent();
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record MgmtApiKeyDto(
    string KeyId,
    string Name,
    bool IsRevoked,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

internal sealed record CreateMgmtApiKeyRequest(
    string? Name = null,
    int? ExpiresInDays = null);

internal sealed record CreateMgmtApiKeyResponse(
    string KeyId,
    string Name,
    string ApiKey,
    DateTimeOffset? ExpiresAt);
