using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Regression suite for the impersonation caller-identity claim-order bug.
///
/// <para>
/// The management impersonation endpoints resolved the caller's user id as
/// <c>NameIdentifier ?? sub</c>. For <b>API-key callers</b> the
/// <see cref="ClaimTypes.NameIdentifier"/> claim is the <i>key id</i>
/// (<c>ApiKeyAuthenticationHandler</c> populates it from <c>apiKey.KeyId.Value</c>),
/// while the owning user lives in the <c>user_id</c> claim. So a management key
/// whose key id differs from its owning user id resolved to the key id, the
/// per-tenant permission lookup found nothing, and the impersonate check
/// failed closed (403) — management keys could never impersonate.
/// </para>
///
/// <para>
/// Canonical order (used by <c>PlatformAdminAuthorizationHandler</c> /
/// <c>PermissionAuthorizationHandler</c> and the P2b <c>TypificationEndpoints</c>
/// fix) is <c>user_id ?? NameIdentifier ?? sub</c>.
/// </para>
/// </summary>
public sealed class ManagementImpersonationCallerResolutionTests
{
    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    [Fact]
    public void ResolveCallerUserId_ShouldResolveOwningUser_WhenCallerIsApiKey()
    {
        // A management API key: NameIdentifier == key id (distinct from the owning
        // user), owning user in the `user_id` claim. The owning user MUST win.
        var principal = PrincipalWith(
            (ClaimTypes.NameIdentifier, "api-key-id-999"),
            ("user_id", "owning-user-123"));

        var resolved = ManagementImpersonationEndpoints.ResolveCallerUserId(principal);

        resolved.Should().Be(
            "owning-user-123",
            because: "for API-key callers the owning user is in `user_id`, not NameIdentifier (the key id)");
    }

    [Fact]
    public void ResolveCallerUserId_ShouldFallBackToNameIdentifier_WhenNoUserIdClaim()
    {
        // A plain JWT user principal: no `user_id` claim, identity in NameIdentifier.
        var principal = PrincipalWith((ClaimTypes.NameIdentifier, "jwt-user-1"));

        ManagementImpersonationEndpoints.ResolveCallerUserId(principal).Should().Be("jwt-user-1");
    }

    [Fact]
    public void ResolveCallerUserId_ShouldFallBackToSub_WhenOnlySubPresent()
    {
        var principal = PrincipalWith(("sub", "subject-1"));

        ManagementImpersonationEndpoints.ResolveCallerUserId(principal).Should().Be("subject-1");
    }

    [Fact]
    public void ResolveCallerUserId_ShouldReturnNull_WhenNoIdentityClaims()
    {
        var principal = PrincipalWith(("tenant_id", "t-1"));

        ManagementImpersonationEndpoints.ResolveCallerUserId(principal).Should().BeNull();
    }
}
