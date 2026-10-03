using System.Security.Claims;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class PermissionAuthorizationHandlerTests
{
    private readonly IUserRoleStore _userRoleStore = Substitute.For<IUserRoleStore>();
    private readonly PermissionAuthorizationHandler _sut;

    public PermissionAuthorizationHandlerTests()
    {
        var resolver = new PermissionResolver(_userRoleStore);
        _sut = new PermissionAuthorizationHandler(resolver);
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenUserHasPermission()
    {
        var permissions = new HashSet<string> { "campaigns:campaign:view" };
        _userRoleStore.GetEffectivePermissionsAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(permissions);

        var requirement = new PermissionRequirement("campaigns:campaign:view");
        var user = CreateUser("tenant-1", "user-1", "Agent");
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ShouldNotSucceed_WhenUserLacksPermission()
    {
        var permissions = new HashSet<string> { "contacts:conversation:handle" };
        _userRoleStore.GetEffectivePermissionsAsync(
                Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(permissions);

        var requirement = new PermissionRequirement("campaigns:campaign:delete");
        var user = CreateUser("tenant-1", "user-1", "Agent");
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenUserHasAdminRole()
    {
        // Admin role gets automatic access (backward compat)
        var requirement = new PermissionRequirement("system:cluster:manage");
        var user = CreateUser("tenant-1", "user-1", "Admin");
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenJwtCarriesPermissionClaim()
    {
        var requirement = new PermissionRequirement("analytics:cdr:view");
        var claims = new[]
        {
            new Claim("tenant_id", "tenant-1"),
            new Claim("permissions", "analytics:cdr:view"),
            new Claim("permissions", "analytics:cdr:export"),
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "JWT"));
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_ShouldNotSucceed_WhenImpersonationTokenLacksPermissionDespiteAdminRole(bool readOnly)
    {
        var requirement = new PermissionRequirement("features:agent-assist:manage");
        var context = new AuthorizationHandlerContext([requirement], Impersonation(readOnly, "contacts:contact:view"), null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse(because: "an impersonation token holds the permissions minted into it, not those of its Admin role");
    }

    [Fact]
    public async Task HandleAsync_ShouldNotResolveRoles_WhenTokenIsImpersonation()
    {
        // Even were the impersonator to hold the permission through a role in the target tenant, the
        // token passes only on what was minted into it.
        _userRoleStore.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "features:agent-assist:manage" });
        var requirement = new PermissionRequirement("features:agent-assist:manage");
        var context = new AuthorizationHandlerContext([requirement], Impersonation(readOnly: false, "contacts:contact:view"), null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
        await _userRoleStore.DidNotReceiveWithAnyArgs().GetEffectivePermissionsAsync(default, default, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_ShouldSucceed_WhenImpersonationTokenCarriesPermission(bool readOnly)
    {
        var requirement = new PermissionRequirement("contacts:contact:view");
        var context = new AuthorizationHandlerContext([requirement], Impersonation(readOnly, "contacts:contact:view"), null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenAdminApiKeyHasNoPermissionClaims()
    {
        var requirement = new PermissionRequirement("features:agent-assist:manage");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "key-1"),
            new Claim("tenant_id", "tenant-1"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim("key_type", "management"),
        };
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")), null);

        await _sut.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue(because: "the Admin role still satisfies a permission for API keys and ordinary tokens");
    }

    private static ClaimsPrincipal Impersonation(bool readOnly, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("sub", "platform-admin"),
            new("tid", "customer-target"),
            new("role", "Admin"),
            new("impersonation", "true"),
            new("impersonator_id", "platform-admin"),
            new("impersonator_tenant", "platform"),
        };
        if (readOnly)
            claims.Add(new Claim("readonly", "true"));
        claims.AddRange(permissions.Select(p => new Claim("permissions", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "JWT"));
    }

    private static ClaimsPrincipal CreateUser(string tenantId, string userId, string role)
    {
        var claims = new[]
        {
            new Claim("tenant_id", tenantId),
            new Claim("user_id", userId),
            new Claim(ClaimTypes.Role, role),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));
    }
}
