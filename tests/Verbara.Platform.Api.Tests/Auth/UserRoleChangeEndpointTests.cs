using System.Net;
using System.Net.Http.Json;
using FluentAssertions.Execution;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// What changing a user's role through PUT /admin/users/{id} does besides writing the role: the
/// change is audited, a lower role ends the user's refresh-token lineage, and the user's RBAC roles —
/// the only source of server-side permissions — move from the role the old role granted to the one
/// the new role grants, so a demoted administrator keeps none of the old role's permissions.
/// </summary>
public sealed class UserRoleChangeEndpointTests : IClassFixture<RoleChangeApiFactory>
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;
    private const string Platform = AccountStatusApiFactory.PlatformTenantId;

    private const string AuditView = "system:audit:view";
    private const string MfaManage = "system:mfa:manage";
    private const string ContactView = "contacts:contact:view";

    private readonly RoleChangeApiFactory _factory;

    public UserRoleChangeEndpointTests(RoleChangeApiFactory factory) => _factory = factory;

    // ─── Audit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUser_ShouldAuditTheRoleChange_WhenTheRoleChanges()
    {
        var target = await NewTargetUserAsync(UserRole.Agent);

        (await PutAsync(target, new { role = "Supervisor" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = (await AuditEntriesAsync(Customer, "user.role_changed"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        entry.ActorId.Should().Be(AccountStatusApiFactory.CustomerAdminUserId);
        entry.TargetType.Should().Be("User");
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["old_role"].Should().Be("Agent");
        entry.Metadata["new_role"].Should().Be("Supervisor");
    }

    [Fact]
    public async Task UpdateUser_ShouldRecordTheRevokedSessionsAsAWarning_WhenTheRoleIsDowngraded()
    {
        var target = await NewTargetUserAsync(UserRole.Admin);
        await SeedRefreshTokensAsync(target, count: 2);

        (await PutAsync(target, new { role = "Agent" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = (await AuditEntriesAsync(Customer, "user.role_changed"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        entry.Severity.Should().Be("warning");
        entry.Metadata!["revoked_sessions"].Should().Be("2");
    }

    [Fact]
    public async Task UpdateUser_ShouldNotAuditARoleChange_WhenOnlyTheDisplayNameChanges()
    {
        var target = await NewTargetUserAsync(UserRole.Agent);

        (await PutAsync(target, new { displayName = "Renamed", role = "Agent" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AuditEntriesAsync(Customer, "user.role_changed")).Should().NotContain(e => e.TargetId == target.UserId.Value);
    }

    // ─── Refresh-token lineage ───────────────────────────────────────────────

    [Theory]
    [InlineData(UserRole.Admin, "Supervisor")]
    [InlineData(UserRole.Admin, "Api")]
    [InlineData(UserRole.Supervisor, "Agent")]
    [InlineData(UserRole.Agent, "Api")]
    public async Task UpdateUser_ShouldRevokeEveryRefreshToken_WhenTheRoleIsDowngraded(UserRole from, string to)
    {
        var target = await NewTargetUserAsync(from);
        await SeedRefreshTokensAsync(target, count: 2);

        (await PutAsync(target, new { role = to })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ActiveRefreshTokensAsync(target)).Should().BeEmpty(
            because: "a lower role ends the lineage, so the next access token is issued at sign-in");
    }

    [Theory]
    [InlineData(UserRole.Api, "Agent")]
    [InlineData(UserRole.Agent, "Supervisor")]
    [InlineData(UserRole.Supervisor, "Admin")]
    public async Task UpdateUser_ShouldKeepRefreshTokens_WhenTheRoleIsUpgraded(UserRole from, string to)
    {
        var target = await NewTargetUserAsync(from);
        await SeedRefreshTokensAsync(target, count: 1);

        (await PutAsync(target, new { role = to })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ActiveRefreshTokensAsync(target)).Should().HaveCount(1);
    }

    [Fact]
    public async Task UpdateUser_ShouldRevokeOnceAndAuditBothChanges_WhenTheRoleIsDowngradedAndTheAccountSuspendedTogether()
    {
        var target = await NewTargetUserAsync(UserRole.Admin);
        await SeedRefreshTokensAsync(target, count: 2);

        (await PutAsync(target, new { role = "Agent", status = "Suspended" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var roleEntry = (await AuditEntriesAsync(Customer, "user.role_changed"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        var statusEntry = (await AuditEntriesAsync(Customer, "user.status_changed"))
            .Should().ContainSingle(e => e.TargetId == target.UserId.Value).Subject;
        using (new AssertionScope())
        {
            (await ActiveRefreshTokensAsync(target)).Should().BeEmpty();
            roleEntry.Metadata!["revoked_sessions"].Should().Be("2");
            statusEntry.Metadata!["revoked_sessions"].Should().Be("2");
        }
    }

    // ─── RBAC roles follow the role ──────────────────────────────────────────

    [Fact]
    public async Task UpdateUser_ShouldMoveTheUserToTheRbacRoleOfItsNewRole_WhenAnAdminIsDemoted()
    {
        await SeedBuiltInRolesAsync(Customer);
        var target = await NewTargetUserAsync(UserRole.Admin);
        await _factory.AssignAsync(target, "admin", "migration");

        (await PutAsync(target, new { role = "Agent" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var grants = await _factory.GrantsOfAsync(target);
        grants.Select(g => g.RoleId).Should().Equal("agent");
        grants[0].AssignedBy.Should().Be(DefaultTenantRole.AssignedBy);
    }

    [Fact]
    public async Task UpdateUser_ShouldMoveTheUserToTheRbacRoleOfItsNewRole_WhenTheRoleIsUpgraded()
    {
        await SeedBuiltInRolesAsync(Customer);
        var target = await NewTargetUserAsync(UserRole.Agent);
        await _factory.AssignAsync(target, "agent", DefaultTenantRole.AssignedBy);

        (await PutAsync(target, new { role = "Admin" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.RoleIdsOfAsync(target)).Should().Equal("admin");
    }

    [Fact]
    public async Task UpdateUser_ShouldRemoveEveryBuiltInAdministratorRole_WhenAnAdminIsDemoted()
    {
        // The roles the platform grants with the Admin role, in every shape they come in: the tenant's
        // copies of the admin-class templates, and the Platform Admin role setup creates.
        await SeedBuiltInRolesAsync(Platform);
        var target = await NewTargetUserAsync(UserRole.Admin, Platform);
        foreach (var roleId in new[] { "admin", "system_admin", $"platform-admin-{Platform}", "partner_admin" })
            await _factory.AssignAsync(target, roleId, assignedBy: null);

        (await PutAsync(target, new { role = "Supervisor" }, Platform)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.RoleIdsOfAsync(target)).Should().Equal("supervisor");
    }

    [Fact]
    public async Task UpdateUser_ShouldKeepCustomRbacRoles_WhenTheRoleChanges()
    {
        // A role an administrator made from the admin template under another name is that
        // administrator's choice, not the role the Admin role grants.
        await SeedBuiltInRolesAsync(Customer);
        await _factory.SaveTenantRoleAsync(Customer, "billing-viewers", "Billing Viewers", "admin", ContactView);
        var target = await NewTargetUserAsync(UserRole.Admin);
        await _factory.AssignAsync(target, "admin", "migration");
        await _factory.AssignAsync(target, "billing-viewers", AccountStatusApiFactory.CustomerAdminUserId);

        (await PutAsync(target, new { role = "Agent" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.RoleIdsOfAsync(target)).Should().BeEquivalentTo(["agent", "billing-viewers"]);
    }

    [Fact]
    public async Task UpdateUser_ShouldRefuseTheFormerRolesPermissions_WhenAnAdminIsDemoted()
    {
        // A host-tenant administrator, granted the Admin role's RBAC role (as the boot-time role
        // migration does), whose permissions are already cached. Once demoted, an access token issued
        // with the new role must not pass a gate the old role's permissions open.
        await SeedBuiltInRolesAsync(Platform);
        var target = await NewTargetUserAsync(UserRole.Admin, Platform);
        await _factory.AssignAsync(target, "admin", "migration");
        var resolver = _factory.Services.GetRequiredService<PermissionResolver>();
        (await resolver.ResolveAsync(target.TenantId, target.UserId, CancellationToken.None))
            .Should().Contain(AuditView, because: "the admin role grants it before the demotion");

        (await PutAsync(target, new { role = "Agent" }, Platform)).StatusCode.Should().Be(HttpStatusCode.OK);

        var demoted = _factory.GetUser(target.UserId.Value, Platform)!;
        demoted.Role.Should().Be(UserRole.Agent);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(demoted));
        using var response = await client.GetAsync("/api/v1/admin/audit/events");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "the demoted user holds the agent role's permissions only");
    }

    [Fact]
    public async Task UpdateUser_ShouldKeepTheRbacRoles_WhenTheRoleIsUnchanged()
    {
        await SeedBuiltInRolesAsync(Customer);
        var target = await NewTargetUserAsync(UserRole.Admin);
        await _factory.AssignAsync(target, "admin", "migration");
        await _factory.AssignAsync(target, "agent", AccountStatusApiFactory.CustomerAdminUserId);

        (await PutAsync(target, new { displayName = "Renamed", role = "Admin" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _factory.RoleIdsOfAsync(target)).Should().BeEquivalentTo(["admin", "agent"]);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's copies of the role templates (id = template id, named like the template), plus
    /// the Platform Admin role setup creates, each with a distinguishing permission set.
    /// </summary>
    private async Task SeedBuiltInRolesAsync(string tenantId)
    {
        await _factory.SaveTenantRoleAsync(tenantId, "agent", "Agent", "agent", ContactView);
        await _factory.SaveTenantRoleAsync(tenantId, "supervisor", "Supervisor", "supervisor", ContactView, "reporting:realtime:view");
        await _factory.SaveTenantRoleAsync(tenantId, "api", "API", "api", "reporting:historical:view");
        await _factory.SaveTenantRoleAsync(tenantId, "admin", "Admin", "admin", ContactView, AuditView, MfaManage);
        await _factory.SaveTenantRoleAsync(tenantId, "system_admin", "System Admin", "system_admin", AuditView, MfaManage);
        await _factory.SaveTenantRoleAsync(tenantId, "partner_admin", "Partner Admin", "partner_admin", "partner:billing:view");
        await _factory.SaveTenantRoleAsync(
            tenantId, $"platform-admin-{tenantId}", "Platform Admin", "platform_admin", AuditView, MfaManage, "billing:credits:grant");
    }

    private Task<User> NewTargetUserAsync(UserRole role, string tenantId = Customer) =>
        Task.FromResult(_factory.SaveUser($"role-change-target-{Guid.NewGuid():N}", tenantId, role, UserStatus.Active));

    private HttpClient AdminClient(string tenantId)
    {
        var adminId = tenantId == Platform ? AccountStatusApiFactory.PlatformAdminUserId : AccountStatusApiFactory.CustomerAdminUserId;
        var admin = _factory.GetUser(adminId, tenantId)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<HttpResponseMessage> PutAsync(User target, object body, string tenantId = Customer)
    {
        using var client = AdminClient(tenantId);
        return await client.PutAsJsonAsync($"/api/v1/admin/users/{target.UserId.Value}", body);
    }

    private async Task SeedRefreshTokensAsync(User user, int count)
    {
        var refreshTokens = _factory.Services.GetRequiredService<RefreshTokenService>();
        for (var i = 0; i < count; i++)
            await refreshTokens.GenerateAsync(user.UserId.Value, user.TenantId.Value, "10.0.0.1", "test", CancellationToken.None);
    }

    private Task<IReadOnlyList<RefreshToken>> ActiveRefreshTokensAsync(User user) =>
        _factory.Services.GetRequiredService<IRefreshTokenStore>()
            .GetActiveByUserAsync(user.TenantId.Value, user.UserId.Value, CancellationToken.None);

    private async Task<IReadOnlyList<AuditEntry>> AuditEntriesAsync(string tenantId, string action)
    {
        using var scope = _factory.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(tenantId), new AuditQuery(Action: action, Page: 1, PageSize: 200), CancellationToken.None);
        return page.Items;
    }
}
