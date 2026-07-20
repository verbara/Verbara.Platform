using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/setup", Setup).AllowAnonymous();
    }

    private static async Task<IResult> Setup(
        [FromBody] SetupRequest body,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IUserStore userStore,
        [FromServices] IApiKeyStore apiKeyStore,
        [FromServices] ITenantRoleStore tenantRoleStore,
        [FromServices] IUserRoleStore userRoleStore,
        [FromServices] JwtTokenService jwtTokenService,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // Guard: only works if no host tenant exists
        var existing = await tenantStore.GetHostTenantAsync(ct);
        if (existing is not null)
            return Results.Conflict(new ErrorResponse("Platform already initialized."));

        // Validate platform admin input
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new ErrorResponse("Email and password are required."));

        // Validate customer input (hard requirement — every install is Platform + Customer)
        if (string.IsNullOrWhiteSpace(body.CustomerTenantId)
            || string.IsNullOrWhiteSpace(body.CustomerName)
            || string.IsNullOrWhiteSpace(body.CustomerAdminEmail)
            || string.IsNullOrWhiteSpace(body.CustomerAdminPassword))
            return Results.BadRequest(new ErrorResponse(
                "Customer tenant id, name, admin email and admin password are required."));

        // Customer tenant id must be a valid slug and must not collide with the host tenant
        var customerTenantIdNormalized = body.CustomerTenantId.Trim().ToLowerInvariant();
        if (!IsValidSlug(customerTenantIdNormalized) || customerTenantIdNormalized == "platform")
            return Results.BadRequest(new ErrorResponse(
                "Customer tenant id must be a lowercase slug (letters, digits, hyphens) and cannot be 'platform'."));

        // Normalize each email once and reuse the normalized value for BOTH the
        // distinct-check below AND persistence (User.Email assignments). The email
        // lookup (IUserStore.GetByEmailAsync) is case-insensitive — InMemory uses
        // OrdinalIgnoreCase, Postgres uses `lower(email) = lower(@Email)` — so a
        // trim + lowercase canonical form is safe: mixed-case login input still
        // resolves, and we avoid persisting two whitespace/case variants that pass
        // the distinct-check yet collide as the same login identity.
        var platformAdminEmail = body.Email.Trim().ToLowerInvariant();
        var customerAdminEmail = body.CustomerAdminEmail.Trim().ToLowerInvariant();

        // The two admins are distinct identities — different emails required
        if (string.Equals(platformAdminEmail, customerAdminEmail, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse(
                "Platform admin and customer admin must use different emails."));

        // Enforce the password policy on BOTH passwords (platform defaults: min 12, upper, number)
        var policyConfig = new TenantAuthConfig { TenantId = "platform" };
        var platformPwdCheck = PasswordService.ValidatePolicy(body.Password, policyConfig);
        if (!platformPwdCheck.IsValid)
            return Results.BadRequest(new ErrorDetailResponse(
                "Platform admin password does not meet policy", platformPwdCheck.Errors));
        var customerPwdCheck = PasswordService.ValidatePolicy(body.CustomerAdminPassword, policyConfig);
        if (!customerPwdCheck.IsValid)
            return Results.BadRequest(new ErrorDetailResponse(
                "Customer admin password does not meet policy", customerPwdCheck.Errors));

        // NON-ATOMIC FIRST-RUN WINDOW: the writes below (host tenant → orphan
        // adoption → platform admin → mgmt key → customer tenant → customer admin)
        // run as sequential awaited calls with NO surrounding transaction. A storage
        // fault mid-sequence can leave a partially-initialized install (e.g. host
        // tenant persisted but customer admin missing). Because the host tenant is
        // the 409 sentinel checked at the top of this handler, a re-POST to /api/setup
        // cannot repair such a partial state — it just returns "already initialized".
        // This is an accepted trade-off per the spec
        // (docs/specs/2026-05-30-setup-multitenant-platform-customer.md): a partial
        // failure surfaces as a 500. A future follow-up may make first-run
        // idempotent/transactional.

        // 1. Create host tenant
        var hostTenantId = "platform";
        var hostTenant = new Tenant
        {
            TenantId = hostTenantId,
            Name = body.PlatformName ?? "Verbara",
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
            Email = platformAdminEmail,
            DisplayName = body.DisplayName ?? "Platform Admin",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(body.Password),
            CreatedAt = clock.UtcNow,
        };
        await userStore.SaveAsync(user, ct);

        // 3.5. Clone the `platform_admin` role template into a tenant role and
        //      assign it to the new platform admin user. Without this the user
        //      only has `UserRole.Admin` fallback permissions (no `platform:*`
        //      perms), which causes the frontend /admin/cluster PermissionGuard
        //      (requires `platform:cluster:manage`) to render a 403 page even
        //      though the backend PlatformAdminOnly policy would accept them.
        //      Failure is silent: any prior step fails are tolerated so Setup
        //      doesn't roll back on optional RBAC wiring.
        try
        {
            var platformAdminRoleId = $"platform-admin-{hostTenantId}";
            await tenantRoleStore.CloneFromTemplateAsync(
                tenantId,
                platformAdminRoleId,
                templateId: "platform_admin",
                name: "Platform Admin",
                description: "Full platform administration including cross-tenant operations",
                ct);
            await userRoleStore.AssignAsync(
                tenantId, userId, platformAdminRoleId, assignedBy: null, ct);
        }
        catch
        {
            // RBAC store may be InMemory/partial in some deployments — the
            // UserRole.Admin fallback still grants day-1 tenant admin perms.
        }

        // 4. Generate Management API Key
        var rawApiKey = SecretTokenGenerator.Mint("mgmt_");
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

        // 5. Generate JWT for the new platform admin
        var (accessToken, _) = jwtTokenService.GenerateAccessToken(user);

        // 6. Create the operational Customer tenant (Type=Customer, parent=platform)
        var customerTenant = new Tenant
        {
            TenantId = customerTenantIdNormalized,
            Name = body.CustomerName,
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = hostTenantId,
            Options = new TenantOptions
            {
                MaxConcurrentChannels = 100,
                MaxActiveCampaigns = 10,
            },
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await tenantStore.UpsertAsync(customerTenant, ct);

        // 7. Create the Customer admin user (lives inside the Customer tenant)
        var customerTenantId = new TenantId(customerTenantIdNormalized);
        var customerUserId = EntityId.New();
        var customerAdmin = new User
        {
            UserId = customerUserId,
            TenantId = customerTenantId,
            Email = customerAdminEmail,
            DisplayName = body.CustomerAdminDisplayName ?? "Administrator",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            PasswordHash = PasswordService.HashPassword(body.CustomerAdminPassword),
            CreatedAt = clock.UtcNow,
        };
        await userStore.SaveAsync(customerAdmin, ct);

        // 7.5. Best-effort: clone the tenant `admin` role template for the Customer admin.
        //      Same tolerance as the platform admin RBAC wiring — UserRole.Admin
        //      fallback still grants day-1 tenant admin perms if the store is partial.
        try
        {
            var customerAdminRoleId = $"admin-{customerTenantIdNormalized}";
            await tenantRoleStore.CloneFromTemplateAsync(
                customerTenantId,
                customerAdminRoleId,
                templateId: "admin",
                name: "Admin",
                description: "Full administrative access except cluster and auth configuration",
                ct);
            await userRoleStore.AssignAsync(
                customerTenantId, customerUserId, customerAdminRoleId, assignedBy: null, ct);
        }
        catch
        {
            // Tolerated — UserRole.Admin fallback grants day-1 tenant admin perms.
        }

        return Results.Created("/management/system/info", new SetupResponse(
            hostTenantId,
            userId.Value,
            accessToken,
            rawApiKey,
            customerTenantIdNormalized,
            customerUserId.Value));
    }

    private static bool IsValidSlug(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63)
            return false;
        foreach (var c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' || c == '-'))
                return false;
        }
        return value[0] != '-' && value[^1] != '-';
    }
}

internal sealed record SetupRequest(
    string Email,
    string Password,
    string? DisplayName,
    string? PlatformName,
    string CustomerTenantId,
    string CustomerName,
    string CustomerAdminEmail,
    string CustomerAdminPassword,
    string? CustomerAdminDisplayName);

internal sealed record SetupResponse(
    string TenantId,
    string UserId,
    string AccessToken,
    string ManagementApiKey,
    string CustomerTenantId,
    string CustomerUserId);
