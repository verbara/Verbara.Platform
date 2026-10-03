using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryUserRoleStore.MoveAsync"/>: one write that takes a user off some roles and onto
/// another, the way <c>PostgresUserRoleStore</c> does it in one statement.
/// </summary>
public sealed class InMemoryUserRoleStoreTests
{
    private static readonly TenantId s_tenant = new("tenant-1");
    private static readonly EntityId s_user = EntityId.From("user-1");

    private readonly InMemoryUserRoleStore _store = new();

    [Fact]
    public async Task MoveAsync_ShouldRemoveTheRolesTheUserHeldAndAssignTheNewOne_WhenGivenBoth()
    {
        await _store.AssignAsync(s_tenant, s_user, "admin", "migration", CancellationToken.None);
        await _store.AssignAsync(s_tenant, s_user, "custom", "an-administrator", CancellationToken.None);

        var removed = await _store.MoveAsync(s_tenant, s_user, ["admin", "system_admin"], "agent", "default-role", CancellationToken.None);

        removed.Should().Equal("admin");
        var grants = await _store.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None);
        grants.Select(g => g.RoleId).Should().BeEquivalentTo(["custom", "agent"]);
        grants.Single(g => g.RoleId == "agent").AssignedBy.Should().Be("default-role");
    }

    [Fact]
    public async Task MoveAsync_ShouldKeepTheNewRoleAsItIs_WhenItIsAlsoAmongTheRolesToRemove()
    {
        await _store.AssignAsync(s_tenant, s_user, "agent", "an-administrator", CancellationToken.None);

        var removed = await _store.MoveAsync(s_tenant, s_user, ["agent"], "agent", "default-role", CancellationToken.None);

        removed.Should().BeEmpty();
        (await _store.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None))
            .Should().ContainSingle().Which.AssignedBy.Should().Be("an-administrator");
    }

    [Fact]
    public async Task MoveAsync_ShouldOnlyRemove_WhenThereIsNoRoleToAssign()
    {
        await _store.AssignAsync(s_tenant, s_user, "admin", "migration", CancellationToken.None);

        var removed = await _store.MoveAsync(s_tenant, s_user, ["admin"], null, "default-role", CancellationToken.None);

        removed.Should().Equal("admin");
        (await _store.GetRolesForUserAsync(s_tenant, s_user, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task MoveAsync_ShouldLeaveOtherUsersAndTenantsAlone_WhenTheyHoldTheSameRoles()
    {
        var otherUser = EntityId.From("user-2");
        var otherTenant = new TenantId("tenant-2");
        await _store.AssignAsync(s_tenant, s_user, "admin", "migration", CancellationToken.None);
        await _store.AssignAsync(s_tenant, otherUser, "admin", "migration", CancellationToken.None);
        await _store.AssignAsync(otherTenant, s_user, "admin", "migration", CancellationToken.None);

        await _store.MoveAsync(s_tenant, s_user, ["admin"], "agent", "default-role", CancellationToken.None);

        (await _store.GetRolesForUserAsync(s_tenant, otherUser, CancellationToken.None)).Should().ContainSingle(g => g.RoleId == "admin");
        (await _store.GetRolesForUserAsync(otherTenant, s_user, CancellationToken.None)).Should().ContainSingle(g => g.RoleId == "admin");
    }
}
