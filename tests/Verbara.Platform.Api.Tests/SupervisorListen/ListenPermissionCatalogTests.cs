using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (verifier finding 6, ADR-0037 pattern): the permission every audio-listen route checks is
/// a catalogued id, and the seeded <c>supervisor</c> role template satisfies the gate through the real
/// <see cref="PermissionAuthorizationHandler"/> (an exact set-membership test, with no implication layer), while the
/// <c>agent</c> template does not.
/// </summary>
public sealed class ListenPermissionCatalogTests
{
    [Fact]
    public void MonitorPermission_ShouldBeCatalogued_ByPermissionSeeder()
    {
        PermissionCatalog.Contains(SupervisorListenEndpoints.MonitorPermission).Should().BeTrue(
            "an uncatalogued gate id can never be granted, so the listen routes would answer not-permitted to everyone");
    }

    [Theory]
    [InlineData("supervisor", true)]
    [InlineData("manager", true)]
    [InlineData("agent", false)]
    public async Task SeededTemplate_ShouldPassTheListenGate_OnlyWhenItGrantsMonitor(string templateId, bool passes)
    {
        var granted = RoleTemplateSeeder.GetTemplatePermissions(templateId);
        granted.Should().NotBeEmpty($"the {templateId} template is seeded");
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>(granted, StringComparer.Ordinal));
        var handler = new PermissionAuthorizationHandler(new PermissionResolver(roles));
        var requirement = new PermissionRequirement(SupervisorListenEndpoints.MonitorPermission);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", "acme"),
            new Claim("user_id", "7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d"),
            new Claim(ClaimTypes.Role, "Supervisor"),
        ], "test"));
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().Be(passes);
    }
}
