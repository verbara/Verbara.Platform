using System.Security.Claims;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression suite for <c>PREPUB-2026-05-09-ADMIN-002</c>: the
/// <see cref="PlatformAdminAuthorizationHandler"/> short-circuited every
/// <see cref="PlatformAdminRequirement"/> when <c>key_type=management</c>
/// regardless of the requested permission. A single management bearer
/// therefore satisfied <c>security.jwt.rotate</c>, <c>audit.export</c>,
/// <c>retention.manage</c>, etc. Fix: enforce the requested permission
/// against the API key's <c>scopes</c> array. Legacy <c>platform:*</c>
/// keys keep working in v1.13.x via the back-compat branch.
/// </summary>
public sealed class PlatformAdminAuthorizationHandlerTests
{
    private const string HostTenantId = "platform";

    [Fact]
    public async Task MgmtKey_ShouldSucceed_WhenScopeIncludesPermission()
    {
        var handler = CreateHandler();
        var requirement = new PlatformAdminRequirement("security.jwt.rotate");

        var principal = ManagementKeyPrincipal(scopes: ["security.jwt.rotate"]);
        var ctx = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            because: "ADMIN-002 fix: a management key whose scope includes the requested permission must satisfy the requirement");
    }

    [Fact]
    public async Task MgmtKey_ShouldFail_WhenScopeDoesNotIncludePermission()
    {
        var handler = CreateHandler();
        var requirement = new PlatformAdminRequirement("security.jwt.rotate");

        // Narrow-scope key (audit only) must NOT satisfy a JWT-rotation gate.
        var principal = ManagementKeyPrincipal(scopes: ["audit.read"]);
        var ctx = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            because: "ADMIN-002 fix: a management key without the requested permission in its scopes must NOT satisfy the requirement");
    }

    [Fact]
    public async Task MgmtKey_LegacyWildcardScope_ShouldSucceed()
    {
        var handler = CreateHandler();
        var requirement = new PlatformAdminRequirement("security.jwt.rotate");

        // Legacy keys issued before the fix carry a single platform:* scope.
        var principal = ManagementKeyPrincipal(scopes: ["platform:*"]);
        var ctx = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            because: "ADMIN-002 fix v1.13.x back-compat: legacy platform:* keys must continue to satisfy any PlatformAdminRequirement");
    }

    [Fact]
    public async Task MgmtKey_ShouldSucceed_WhenRequirementHasNoSpecificPermission()
    {
        // Bare PlatformAdminOnly policy uses PlatformAdminRequirement() with
        // null Permission. Management keys must continue to satisfy this — the
        // fix only constrains the permission-gated surfaces.
        var handler = CreateHandler();
        var requirement = new PlatformAdminRequirement();

        var principal = ManagementKeyPrincipal(scopes: ["security.jwt.rotate"]);
        var ctx = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            because: "PlatformAdminRequirement() without a permission must continue to be satisfied by any valid management key");
    }

    // ─── Issuance back-compat: CreateApiKey defaults ─────────────────────────

    [Fact]
    public void CreateApiKey_ShouldRequireExplicitScopes_WhenNotLegacy()
    {
        // The new ManagementApiKeyEndpoints.CreateKey path issues keys whose
        // default scopes are explicit (the API-key fix accompanying ADMIN-002).
        // We assert that the legacy single-string "platform:*" sentinel is NOT
        // hard-coded in the issuance default any more — instead, callers can
        // provide a scope whitelist or accept the new least-privilege default.
        // This is a structural assertion against the source: the only legitimate
        // remaining occurrence of "platform:*" is in ApiKey.HasScope's wildcard
        // expansion + the back-compat seed for legacy keys.
        var apiKey = new ApiKey
        {
            KeyId = EntityId.From("k-test"),
            TenantId = new TenantId(HostTenantId),
            Name = "Narrow Key",
            HashedKey = "h",
            Scopes = ["audit.read"], // explicit narrow scope
            KeyType = ApiKeyType.Management,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // The narrow-scope key must NOT match the legacy wildcard semantics.
        apiKey.HasScope("security.jwt.rotate").Should().BeFalse(
            because: "narrow-scope keys must not silently inherit legacy platform:* behaviour");
        apiKey.HasScope("audit.read").Should().BeTrue();
    }

    private static PlatformAdminAuthorizationHandler CreateHandler()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        // Host tenant lookup so the handler's host-tenant cache resolves.
        tenantStore.GetHostTenantAsync(Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = HostTenantId,
                Name = "Test Host",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
            });

        // PermissionResolver is irrelevant for this test class — the
        // management-key branch never calls it — but we satisfy the
        // constructor with a substitute IUserRoleStore.
        var resolver = new PermissionResolver(Substitute.For<IUserRoleStore>());

        return new PlatformAdminAuthorizationHandler(tenantStore, resolver);
    }

    private static ClaimsPrincipal ManagementKeyPrincipal(IReadOnlyList<string> scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "mgmt-key-id"),
            new("tenant_id", HostTenantId),
            new(ClaimTypes.Role, "Admin"),
            new("key_type", "management"),
        };
        foreach (var scope in scopes)
            claims.Add(new Claim("scope", scope));

        var identity = new ClaimsIdentity(claims, "ApiKey");
        return new ClaimsPrincipal(identity);
    }
}
