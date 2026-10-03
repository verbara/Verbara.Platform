using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Verbara.Platform.Identity.Tests;

/// <summary>
/// The role a new user is granted for its <see cref="UserRole"/>: the tenant's copy of the role
/// template, resolved the way the boot-time RBAC migration resolves it (the role whose id is the
/// template id, else the one named like the template), and cloned from the template when the tenant
/// has neither — so the user and that migration settle on the same single role.
/// </summary>
public sealed class DefaultTenantRoleTests
{
    private static readonly TenantId s_tenant = new("tenant-1");
    private static readonly EntityId s_user = EntityId.From("user-1");

    private readonly InMemoryTenantRoleStore _tenantRoles = new();
    private readonly InMemoryUserRoleStore _userRoles = new();
    private readonly IRoleTemplateStore _templates = Substitute.For<IRoleTemplateStore>();

    public DefaultTenantRoleTests()
    {
        _templates.GetByIdAsync("agent", Arg.Any<CancellationToken>()).Returns(new RoleTemplate
        {
            TemplateId = "agent",
            Name = "Agent",
            Description = "Frontline agent handling conversations",
            IsSystem = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Theory]
    [InlineData(UserRole.Agent, "agent")]
    [InlineData(UserRole.Supervisor, "supervisor")]
    [InlineData(UserRole.Admin, "admin")]
    [InlineData(UserRole.Api, "api")]
    public void TemplateIdFor_ShouldNameTheTemplateTheBootMigrationMapsTheRoleTo_WhenGivenAnyRole(UserRole role, string templateId) =>
        DefaultTenantRole.TemplateIdFor(role).Should().Be(templateId);

    [Fact]
    public async Task GrantAsync_ShouldGrantTheRoleWithTheTemplatesId_WhenTheTenantHasIt()
    {
        await SaveRoleAsync("agent", "Agent");
        await SaveRoleAsync("supervisor", "Supervisor");

        var granted = await GrantAsync(UserRole.Agent);

        granted.Should().Be("agent");
        await ShouldHoldOnlyAsync("agent");
    }

    [Fact]
    public async Task GrantAsync_ShouldPreferTheTemplatesIdOverTheTemplatesName_WhenTheTenantHasBoth()
    {
        await SaveRoleAsync("agent", "Frontline");
        await SaveRoleAsync("role_agent_tenant-1", "Agent");

        var granted = await GrantAsync(UserRole.Agent);

        granted.Should().Be("agent");
        await ShouldHoldOnlyAsync("agent");
    }

    [Theory]
    [InlineData("role_agent_tenant-1", "Agent")]   // cloned at tenant provisioning
    [InlineData("custom-frontline", "agent")]      // the same name in another case
    public async Task GrantAsync_ShouldGrantTheRoleNamedLikeTheTemplate_WhenNoRoleHasTheTemplatesId(string roleId, string name)
    {
        await SaveRoleAsync(roleId, name);

        var granted = await GrantAsync(UserRole.Agent);

        granted.Should().Be(roleId);
        await ShouldHoldOnlyAsync(roleId);
        (await _tenantRoles.ListAsync(s_tenant, CancellationToken.None)).Should().ContainSingle(
            because: "a tenant that already has the role gets no second copy of the template");
    }

    [Fact]
    public async Task GrantAsync_ShouldNotGrantARoleThatOnlyDerivesFromTheTemplate_WhenItHasAnotherName()
    {
        // A custom role cloned from the template under its own name is an administrator's choice, not
        // the default: the boot-time migration would not attach it either.
        await SaveRoleAsync("senior-agents", "Senior agents", sourceTemplateId: "agent");

        var granted = await GrantAsync(UserRole.Agent);

        granted.Should().Be("agent", because: "the template is cloned under its own id and name instead");
        await ShouldHoldOnlyAsync("agent");
    }

    [Fact]
    public async Task GrantAsync_ShouldCloneTheTemplateAndGrantIt_WhenTheTenantHasNoSuchRole()
    {
        var granted = await GrantAsync(UserRole.Agent);

        granted.Should().Be("agent");
        var role = (await _tenantRoles.GetByIdAsync(s_tenant, "agent", CancellationToken.None))!;
        role.Name.Should().Be("Agent");
        role.SourceTemplateId.Should().Be("agent");
        await ShouldHoldOnlyAsync("agent");
    }

    [Fact]
    public async Task GrantAsync_ShouldGrantNothing_WhenTheTenantHasNoSuchRoleAndTheTemplateIsUnknown()
    {
        var granted = await GrantAsync(UserRole.Supervisor);

        granted.Should().BeNull();
        (await _userRoles.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None)).Should().BeEmpty();
        (await _tenantRoles.ListAsync(s_tenant, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task GrantAsync_ShouldGrantTheRoleAnotherWriterCloned_WhenItsOwnCloneConflicts()
    {
        // Two users created at once in a tenant without the role: the other request's clone lands
        // first, and this one's insert conflicts.
        var tenantRoles = Substitute.For<ITenantRoleStore>();
        var cloned = Role("agent", "Agent");
        tenantRoles.ListAsync(s_tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TenantRole>>([]), Task.FromResult<IReadOnlyList<TenantRole>>([cloned]));
        tenantRoles.CloneFromTemplateAsync(s_tenant, "agent", "agent", "Agent", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("duplicate key value violates unique constraint"));

        var granted = await DefaultTenantRole.GrantAsync(
            tenantRoles, _templates, _userRoles, s_tenant, s_user, UserRole.Agent, CancellationToken.None);

        granted.Should().Be("agent");
        await ShouldHoldOnlyAsync("agent");
    }

    [Fact]
    public async Task GrantAsync_ShouldThrow_WhenTheCloneFailsAndNoRoleAppears()
    {
        var tenantRoles = Substitute.For<ITenantRoleStore>();
        tenantRoles.ListAsync(s_tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TenantRole>>([]));
        tenantRoles.CloneFromTemplateAsync(default, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("database unavailable"));

        var grant = () => DefaultTenantRole.GrantAsync(
            tenantRoles, _templates, _userRoles, s_tenant, s_user, UserRole.Agent, CancellationToken.None);

        await grant.Should().ThrowAsync<InvalidOperationException>().WithMessage("database unavailable");
        (await _userRoles.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None)).Should().BeEmpty();
    }

    private Task<string?> GrantAsync(UserRole role) =>
        DefaultTenantRole.GrantAsync(_tenantRoles, _templates, _userRoles, s_tenant, s_user, role, CancellationToken.None);

    private async Task ShouldHoldOnlyAsync(string roleId)
    {
        var grants = await _userRoles.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None);
        grants.Should().ContainSingle().Which.RoleId.Should().Be(roleId);
        grants[0].AssignedBy.Should().Be(DefaultTenantRole.AssignedBy);
    }

    private Task SaveRoleAsync(string roleId, string name, string? sourceTemplateId = null) =>
        _tenantRoles.SaveAsync(Role(roleId, name, sourceTemplateId), CancellationToken.None);

    private static TenantRole Role(string roleId, string name, string? sourceTemplateId = null) => new()
    {
        RoleId = roleId,
        TenantId = s_tenant,
        Name = name,
        SourceTemplateId = sourceTemplateId ?? "agent",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
