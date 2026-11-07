using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Verbara.Platform.Api.Services.AgentAssist;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant task 1.1 (design D1, D5): the owner tenant of a live Agent Assist session
/// is the one tenant its call and its start-time snapshot agree on, else none; the transcript listen's caller id comes
/// from the credentials, never invented. Pure functions, green on writing.
/// </summary>
public sealed class AgentAssistOwnerTenantTests
{
    [Fact]
    public void OwnerTenantOf_ShouldReturnTheTenant_WhenCallAndSnapshotAgree() =>
        AgentAssistLiveSessions.OwnerTenantOf("acme", "acme").Should().Be("acme");

    [Fact]
    public void OwnerTenantOf_ShouldReturnNull_WhenCallAndSnapshotDiffer() =>
        AgentAssistLiveSessions.OwnerTenantOf("acme", "globex").Should().BeNull("a conflict picks neither tenant");

    [Fact]
    public void OwnerTenantOf_ShouldFallBackToTheSessionSnapshot_WhenTheCallTenantIsEmpty()
    {
        AgentAssistLiveSessions.OwnerTenantOf(null, "acme").Should().Be("acme");
        AgentAssistLiveSessions.OwnerTenantOf("", "acme").Should().Be("acme");
        AgentAssistLiveSessions.OwnerTenantOf("  ", "acme").Should().Be("acme");
    }

    [Fact]
    public void OwnerTenantOf_ShouldUseTheCallTenant_WhenTheSnapshotIsEmpty()
    {
        AgentAssistLiveSessions.OwnerTenantOf("acme", null).Should().Be("acme");
        AgentAssistLiveSessions.OwnerTenantOf("acme", "").Should().Be("acme", "the late stamp: the snapshot was taken first");
        AgentAssistLiveSessions.OwnerTenantOf("acme", " ").Should().Be("acme");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", "\t")]
    [InlineData(null, "")]
    [InlineData("  ", null)]
    public void OwnerTenantOf_ShouldReturnNull_WhenNeitherIsSet(string? callTenant, string? sessionTenant) =>
        AgentAssistLiveSessions.OwnerTenantOf(callTenant, sessionTenant).Should().BeNull();

    [Fact]
    public void OwnerTenantOf_ShouldCompareOrdinally_WhenTheTenantsDifferOnlyByCase() =>
        AgentAssistLiveSessions.OwnerTenantOf("acme", "ACME").Should().BeNull();

    [Fact]
    public void CallerUserIdOf_ShouldReturnNull_WhenNoUserIdClaimIsPresent()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Supervisor"), new Claim("tid", "acme")], "test"));

        AgentAssistCaller.CallerUserIdOf(user).Should().BeNull();
    }

    [Fact]
    public void CallerUserIdOf_ShouldPreferSub_WhenSeveralClaimsArePresent()
    {
        var all = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "name-id"),
            new Claim("user_id", "bound-user"),
            new Claim(JwtRegisteredClaimNames.Sub, "jwt-sub"),
        ], "test"));
        var noSub = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "name-id"), new Claim("user_id", "bound-user")], "test"));
        var onlyNameId = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "name-id")], "test"));

        AgentAssistCaller.CallerUserIdOf(all).Should().Be("jwt-sub");
        AgentAssistCaller.CallerUserIdOf(noSub).Should().Be("bound-user");
        AgentAssistCaller.CallerUserIdOf(onlyNameId).Should().Be("name-id");
    }
}
