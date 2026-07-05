using System.Security.Claims;
using Verbara.Platform.Api.Endpoints.Shared;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Unit coverage for the shared canonical caller-identity resolver
/// (audit-trail-integrity-fixes, fix 3) — the SAME precedence
/// <c>ManagementImpersonationCallerResolutionTests</c> locks for
/// <c>ManagementImpersonationEndpoints.ResolveCallerUserId</c> (which now delegates here). Every
/// <c>RecordAudit</c>-style call-site (TypificationEndpoints, ReasonHintEndpoints,
/// ConversationEndpoints) routes through this ONE resolver.
/// </summary>
public sealed class CallerIdentityTests
{
    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    [Fact]
    public void ResolveUserId_ShouldPreferUserId_WhenApiKeyCallerHasDistinctKeyId()
    {
        // API-key caller: NameIdentifier is the KEY id, user_id is the OWNING user. user_id MUST win.
        var principal = PrincipalWith(
            (ClaimTypes.NameIdentifier, "api-key-id-999"),
            ("user_id", "owning-user-123"));

        CallerIdentity.ResolveUserId(principal).Should().Be("owning-user-123");
    }

    [Fact]
    public void ResolveUserId_ShouldFallBackToNameIdentifier_WhenNoUserIdClaim()
    {
        var principal = PrincipalWith((ClaimTypes.NameIdentifier, "jwt-user-1"));

        CallerIdentity.ResolveUserId(principal).Should().Be("jwt-user-1");
    }

    [Fact]
    public void ResolveUserId_ShouldFallBackToSub_WhenOnlySubPresent()
    {
        var principal = PrincipalWith(("sub", "subject-1"));

        CallerIdentity.ResolveUserId(principal).Should().Be("subject-1");
    }

    [Fact]
    public void ResolveUserId_ShouldReturnNull_WhenNoIdentityClaims()
    {
        var principal = PrincipalWith(("tenant_id", "t-1"));

        CallerIdentity.ResolveUserId(principal).Should().BeNull();
    }

    [Fact]
    public void ResolveUserIdOrSystem_ShouldReturnSystem_WhenNoIdentityClaims()
    {
        var principal = PrincipalWith(("tenant_id", "t-1"));

        CallerIdentity.ResolveUserIdOrSystem(principal).Should().Be("system");
    }

    [Fact]
    public void ResolveUserIdOrSystem_ShouldReturnResolvedId_WhenSubPresent()
    {
        // The exact scenario the bug affected: an authenticated caller whose principal carries
        // only `sub` (no user_id, no NameIdentifier) — must NOT collapse to "system".
        var principal = PrincipalWith(("sub", "jwt-subject-42"));

        CallerIdentity.ResolveUserIdOrSystem(principal).Should().Be("jwt-subject-42");
    }
}
