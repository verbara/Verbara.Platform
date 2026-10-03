using System.Security.Claims;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The per-request account-status rule for impersonation tokens (the bearer pipeline applies it;
/// <see cref="ImpersonationTokenStatusTests"/> and <see cref="ImpersonatorRoleTests"/> cover it end to
/// end). It reads the impersonator's home account, never the target tenant the token's <c>tid</c>
/// names, and holds the token to the role its impersonator had when it was minted.
/// </summary>
public sealed class AccountStatusGateTests
{
    private const string HomeTenant = "platform";
    private const string AdminId = "admin-1";

    [Fact]
    public void IsImpersonation_ShouldBeTrue_WhenTheTokenCarriesTheImpersonationClaim()
    {
        AccountStatusGate.IsImpersonation(ImpersonationToken()).Should().BeTrue();
    }

    [Fact]
    public void IsImpersonation_ShouldBeFalse_WhenTheTokenIsAnOrdinaryAccessToken()
    {
        var accessToken = Principal(new Claim("sub", AdminId), new Claim("tid", HomeTenant));

        AccountStatusGate.IsImpersonation(accessToken).Should().BeFalse(
            because: "an ordinary access token is bounded by its 15-minute lifetime and never looked up");
    }

    [Theory]
    [InlineData(UserStatus.Active, true)]
    [InlineData(UserStatus.Suspended, false)]
    [InlineData(UserStatus.Deactivated, false)]
    public async Task ImpersonatorMayAuthenticateAsync_ShouldApplyTheAccountStatusRule_WhenTheImpersonatorExists(
        UserStatus status, bool expected)
    {
        var users = UsersHolding(Admin(status));

        (await AccountStatusGate.ImpersonatorMayAuthenticateAsync(ImpersonationToken(), users, CancellationToken.None))
            .Should().Be(expected);
        await users.Received(1).GetByIdAsync(
            new TenantId(HomeTenant), EntityId.From(AdminId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImpersonatorMayAuthenticateAsync_ShouldBeFalse_WhenTheImpersonatorNoLongerExists()
    {
        var users = UsersHolding(null);

        (await AccountStatusGate.ImpersonatorMayAuthenticateAsync(ImpersonationToken(), users, CancellationToken.None))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("impersonator_tenant")]
    [InlineData("impersonator_id")]
    [InlineData("impersonator_role")]
    public async Task ImpersonatorMayAuthenticateAsync_ShouldBeFalseWithoutALookup_WhenTheTokenNamesNoImpersonator(string missingClaim)
    {
        var users = UsersHolding(Admin(UserStatus.Active));
        var claims = ImpersonationToken().Claims.Where(c => c.Type != missingClaim).ToArray();

        (await AccountStatusGate.ImpersonatorMayAuthenticateAsync(Principal(claims), users, CancellationToken.None))
            .Should().BeFalse(because: "every impersonation token Platform.Api mints names its impersonator and that one's role");
        await users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default, default);
    }

    [Theory]
    [InlineData(UserRole.Supervisor)]
    [InlineData(UserRole.Agent)]
    [InlineData(UserRole.Api)]
    public async Task ImpersonatorMayAuthenticateAsync_ShouldBeFalse_WhenTheImpersonatorNoLongerHoldsTheMintedRole(UserRole currentRole)
    {
        var users = UsersHolding(Admin(UserStatus.Active, currentRole));

        (await AccountStatusGate.ImpersonatorMayAuthenticateAsync(ImpersonationToken(), users, CancellationToken.None))
            .Should().BeFalse(because: "the token was minted for an Admin, and its impersonator is not one any more");
    }

    [Fact]
    public async Task ImpersonatorMayAuthenticateAsync_ShouldBeTrue_WhenTheImpersonatorStillHoldsTheMintedRole()
    {
        // A management key's owner need not be an Admin: the token is held to the role it was minted for.
        var users = UsersHolding(Admin(UserStatus.Active, UserRole.Supervisor));

        (await AccountStatusGate.ImpersonatorMayAuthenticateAsync(ImpersonationToken("Supervisor"), users, CancellationToken.None))
            .Should().BeTrue();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ClaimsPrincipal ImpersonationToken(string impersonatorRole = "Admin") =>
        Principal(
            new Claim("sub", AdminId),
            new Claim("tid", "customer-x"),
            new Claim("impersonation", "true"),
            new Claim("impersonator_id", AdminId),
            new Claim("impersonator_tenant", HomeTenant),
            new Claim("impersonator_role", impersonatorRole));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    private static User Admin(UserStatus status, UserRole role = UserRole.Admin) => new()
    {
        UserId = EntityId.From(AdminId),
        TenantId = new TenantId(HomeTenant),
        Email = "admin@example.com",
        DisplayName = "Admin",
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static IUserStore UsersHolding(User? user)
    {
        var users = Substitute.For<IUserStore>();
        users.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(user));
        return users;
    }
}
