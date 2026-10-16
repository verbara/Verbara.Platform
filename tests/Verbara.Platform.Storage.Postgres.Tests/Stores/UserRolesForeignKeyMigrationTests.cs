using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// add-user-roles-user-foreign-key (design D1-D4): migration <c>024_UserRolesUserForeignKey.sql</c> against a
/// real Postgres — it deletes the orphan <c>user_roles</c> rows itself and adds the key onto <c>users</c> with
/// <c>ON DELETE CASCADE</c>, so an orphan insert fails with 23503 and deleting a user removes that user's role
/// assignments.
/// </summary>
public sealed class UserRolesForeignKeyMigrationTests : IClassFixture<UserRolesForeignKeyFixture>
{
    private readonly UserRolesForeignKeyFixture _fixture;

    public UserRolesForeignKeyMigrationTests(UserRolesForeignKeyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration024_ShouldRemoveTheOrphansKeepTheValidRowsAndAddTheKey_WhenOrphansExistInTwoTenants()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: true);
        await SeedTenantAsync(ds, "t1", users: ["u1", "u2"]);
        await SeedTenantAsync(ds, "t2", users: ["u3"]);
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "agent");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u2", "admin");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t2", "u3", "agent");
        // Orphans: two in t1 (one of them a user id that exists only in t2), one in t2.
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "gone-1", "agent");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u3", "admin");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t2", "gone-2", "admin");

        await UserRolesForeignKeyFixture.ApplyMigrationAsync(ds, UserRolesForeignKeyFixture.Migration024);

        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(
            ["t1|u1|agent", "t1|u2|admin", "t2|u3|agent"], "every row whose user exists is kept");
        (await UserRolesForeignKeyFixture.UserForeignKeyAsync(ds)).Should().Be(("user_roles_tenant_id_user_id_fkey", 'c'));
    }

    [Fact]
    public async Task Migration024_ShouldAddTheKeyAndRemoveNothing_WhenTheDatabaseHasNoOrphans()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: true);
        await SeedTenantAsync(ds, "t1", users: ["u1"]);
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "agent");

        await UserRolesForeignKeyFixture.ApplyMigrationAsync(ds, UserRolesForeignKeyFixture.Migration024);

        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(["t1|u1|agent"]);
        (await UserRolesForeignKeyFixture.UserForeignKeyAsync(ds)).Should().NotBeNull();
    }

    [Fact]
    public async Task Migration024_ShouldSucceedAndChangeNothing_WhenAppliedTwice()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: false);
        await SeedTenantAsync(ds, "t1", users: ["u1"]);
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "agent");

        await UserRolesForeignKeyFixture.ApplyMigrationAsync(ds, UserRolesForeignKeyFixture.Migration024);

        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(["t1|u1|agent"]);
        (await UserRolesForeignKeyFixture.UserForeignKeyAsync(ds)).Should().Be(("user_roles_tenant_id_user_id_fkey", 'c'));
    }

    [Fact]
    public async Task InsertUserRole_ShouldFailOnTheForeignKey_WhenTheUserDoesNotExistInTheTenant()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: false);
        await SeedTenantAsync(ds, "t1", users: ["u1"]);
        await SeedTenantAsync(ds, "t2", users: ["u2"]);

        var orphan = () => UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "nobody", "agent");
        var foreign = () => UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u2", "agent");

        var error = (await orphan.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.Should().Be("user_roles_tenant_id_user_id_fkey");
        (await foreign.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.ForeignKeyViolation, "a user of another tenant is not a user of this one");
        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().BeEmpty();
    }

    [Fact]
    public async Task AssignAsync_ShouldThrowForeignKeyViolationAndWriteNothing_WhenTheUserDoesNotExist()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: false);
        await SeedTenantAsync(ds, "t1", users: ["u1"]);
        var store = new PostgresUserRoleStore(ds);

        await store.AssignAsync(new TenantId("t1"), EntityId.From("u1"), "agent", "admin-1", CancellationToken.None);
        var orphan = () => store.AssignAsync(new TenantId("t1"), EntityId.From("nobody"), "agent", "admin-1", CancellationToken.None);

        (await orphan.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(["t1|u1|agent"]);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheUsersRoleAssignmentsOnly_WhenTheUserIsDeleted()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: false);
        await SeedTenantAsync(ds, "t1", users: ["u1", "u2"]);
        await SeedTenantAsync(ds, "t2", users: ["u1"]);
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "agent");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "admin");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u2", "agent");
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t2", "u1", "agent");

        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            (await PostgresUserStore.DeleteAsync(conn, tx, new TenantId("t1"), EntityId.From("u1"), CancellationToken.None))
                .Should().BeTrue();
            await tx.CommitAsync();
        }

        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(
            ["t1|u2|agent", "t2|u1|agent"], "the deleted user's rows go with it; the same id in another tenant is another user");
    }

    [Fact]
    public async Task DeleteAsync_ShouldKeepTheRoleAssignments_WhenTheDeleteRollsBack()
    {
        var ds = await _fixture.CreateDatabaseAsync(before024: false);
        await SeedTenantAsync(ds, "t1", users: ["u1"]);
        await UserRolesForeignKeyFixture.InsertUserRoleAsync(ds, "t1", "u1", "agent");

        await using (var conn = await ds.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await PostgresUserStore.DeleteAsync(conn, tx, new TenantId("t1"), EntityId.From("u1"), CancellationToken.None);
            await tx.RollbackAsync();
        }

        (await UserRolesForeignKeyFixture.AssignmentsAsync(ds)).Should().Equal(["t1|u1|agent"]);
    }

    private static async Task SeedTenantAsync(NpgsqlDataSource ds, string tenantId, string[] users)
    {
        await UserRolesForeignKeyFixture.InsertRoleAsync(ds, tenantId, "agent");
        await UserRolesForeignKeyFixture.InsertRoleAsync(ds, tenantId, "admin");
        foreach (var user in users)
            await UserRolesForeignKeyFixture.InsertUserAsync(ds, tenantId, user);
    }
}
