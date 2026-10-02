using System.Security.Claims;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class LiveConnectionOwnerTests
{
    [Fact]
    public void FromPrincipal_ShouldUseSubAndTid_WhenPrincipalIsAnAccessToken()
    {
        var owner = LiveConnectionOwner.FromPrincipal(Principal(("sub", "user-1"), ("tid", "acme")));

        owner.Should().Be(new LiveConnectionOwner("acme", "user-1"));
    }

    [Fact]
    public void FromPrincipal_ShouldUseTheImpersonatorsHomeTenant_WhenPrincipalIsAnImpersonationToken()
    {
        // An impersonation token carries the TARGET tenant in `tid`; the account it belongs to lives
        // in `impersonator_tenant`, which is where its status — and its revocation — is keyed.
        var owner = LiveConnectionOwner.FromPrincipal(Principal(
            ("sub", "admin-1"), ("tid", "customer-x"),
            ("impersonator_id", "admin-1"), ("impersonator_tenant", "platform")));

        owner.Should().Be(new LiveConnectionOwner("platform", "admin-1"));
    }

    [Fact]
    public void FromPrincipal_ShouldUseUserIdAndTenantId_WhenPrincipalIsAUserBoundApiKey()
    {
        // NameIdentifier carries the KEY id on API-key principals and must never be read as a user.
        var owner = LiveConnectionOwner.FromPrincipal(Principal(
            (ClaimTypes.NameIdentifier, "key-1"), ("tenant_id", "acme"), ("user_id", "user-1")));

        owner.Should().Be(new LiveConnectionOwner("acme", "user-1"));
    }

    [Fact]
    public void FromPrincipal_ShouldReturnNull_WhenPrincipalIsAnOwnerlessManagementKey()
    {
        var owner = LiveConnectionOwner.FromPrincipal(Principal(
            (ClaimTypes.NameIdentifier, "key-1"), ("tenant_id", "platform"), ("key_type", "management")));

        owner.Should().BeNull();
    }

    [Fact]
    public void FromPrincipal_ShouldReturnNull_WhenTenantIsMissing()
    {
        LiveConnectionOwner.FromPrincipal(Principal(("sub", "user-1"))).Should().BeNull();
    }

    [Fact]
    public void FromPrincipal_ShouldReturnNull_WhenPrincipalIsNull()
    {
        LiveConnectionOwner.FromPrincipal(null).Should().BeNull();
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
