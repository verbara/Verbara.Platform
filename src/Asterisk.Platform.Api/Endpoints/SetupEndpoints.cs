using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/setup", Setup).AllowAnonymous();
    }

    private static async Task<IResult> Setup(
        [FromBody] SetupRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] JwtTokenService jwtTokenService,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // Guard: only works if no host tenant exists
        var existing = await tenantStore.GetHostTenantAsync(ct);
        if (existing is not null)
            return Results.Conflict(new { error = "Platform already initialized." });

        // Validate input
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "Email and password are required." });

        // 1. Create host tenant
        var hostTenantId = "platform";
        var hostTenant = new Tenant
        {
            TenantId = hostTenantId,
            Name = body.PlatformName ?? "Asterisk Platform",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await tenantStore.UpsertAsync(hostTenant, ct);

        // 2. Adopt orphan tenants (existing Customer tenants with no parent)
        var allActive = await tenantStore.GetAllActiveAsync(ct);
        foreach (var orphan in allActive)
        {
            if (orphan.TenantId != hostTenantId && orphan.ParentTenantId is null)
            {
                var adopted = new Tenant
                {
                    TenantId = orphan.TenantId,
                    Name = orphan.Name,
                    Status = orphan.Status,
                    Options = orphan.Options,
                    Metadata = orphan.Metadata,
                    CreatedAt = orphan.CreatedAt,
                    UpdatedAt = clock.UtcNow,
                    ParentTenantId = hostTenantId,
                    Type = TenantType.Customer,
                };
                await tenantStore.UpsertAsync(adopted, ct);
            }
        }

        // 3. Create platform admin user
        var tenantId = new TenantId(hostTenantId);
        var userId = EntityId.New();
        var user = new User
        {
            UserId = userId,
            TenantId = tenantId,
            Email = body.Email,
            DisplayName = body.DisplayName ?? "Platform Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(body.Password),
            CreatedAt = clock.UtcNow,
        };
        await userStore.SaveAsync(user, ct);

        // 4. Generate Management API Key
        var rawApiKey = $"mgmt_{Guid.NewGuid():N}";
        var hashedKey = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey)));
        var mgmtKey = new ApiKey
        {
            KeyId = EntityId.New(),
            TenantId = tenantId,
            Name = "Platform Management Key",
            HashedKey = hashedKey,
            Scopes = ["platform:*"],
            KeyType = ApiKeyType.Management,
            CreatedAt = clock.UtcNow,
        };
        await apiKeyStore.SaveAsync(mgmtKey, ct);

        // 5. Generate JWT for the new admin
        var (accessToken, _) = jwtTokenService.GenerateAccessToken(user);

        return Results.Created("/api/management/system/info", new SetupResponse(
            hostTenantId,
            userId.Value,
            accessToken,
            rawApiKey));
    }
}

internal sealed record SetupRequest(
    string Email,
    string Password,
    string? DisplayName,
    string? PlatformName);

internal sealed record SetupResponse(
    string TenantId,
    string UserId,
    string AccessToken,
    string ManagementApiKey);
