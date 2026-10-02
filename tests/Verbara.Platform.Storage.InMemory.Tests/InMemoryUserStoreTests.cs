using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// The role/status write contract of <see cref="IUserStore"/>, held by the in-memory store exactly as
/// by <c>PostgresUserStore</c>: <see cref="IUserStore.SaveAsync"/> inserts a new user with every field
/// but never changes an existing user's role or status, which only
/// <see cref="IUserStore.SetRoleAsync"/> and <see cref="IUserStore.SetStatusAsync"/> write. A caller
/// saving an object it read before a suspension or a demotion therefore cannot undo it.
/// </summary>
public sealed class InMemoryUserStoreTests
{
    private static readonly TenantId Tenant = new("t-users");
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── A save from an earlier snapshot ────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ShouldKeepTheSuspension_WhenAnEarlierSnapshotIsSavedAfterIt()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-status");
        await store.SaveAsync(user, CancellationToken.None);
        var earlierSnapshot = SnapshotOf(user);

        await store.SetStatusAsync(Tenant, user.UserId, UserStatus.Suspended, ChangedAt, CancellationToken.None);

        earlierSnapshot.FailedLoginAttempts++;
        await store.SaveAsync(earlierSnapshot, CancellationToken.None);

        var reloaded = (await store.GetByIdAsync(Tenant, user.UserId, CancellationToken.None))!;
        reloaded.Status.Should().Be(UserStatus.Suspended,
            because: "a save from a snapshot read before the suspension must not undo it");
        reloaded.FailedLoginAttempts.Should().Be(1, because: "the snapshot's own change is still saved");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheRoleChange_WhenAnEarlierSnapshotIsSavedAfterIt()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-role", UserRole.Admin);
        await store.SaveAsync(user, CancellationToken.None);
        var earlierSnapshot = SnapshotOf(user);

        await store.SetRoleAsync(Tenant, user.UserId, UserRole.Agent, ChangedAt, CancellationToken.None);

        earlierSnapshot.FailedLoginAttempts++;
        await store.SaveAsync(earlierSnapshot, CancellationToken.None);

        (await store.GetByIdAsync(Tenant, user.UserId, CancellationToken.None))!.Role
            .Should().Be(UserRole.Agent, because: "a save from a snapshot read before the demotion must not restore the old role");
    }

    // ─── SaveAsync never writes role or status of an existing user ──────────

    [Fact]
    public async Task SaveAsync_ShouldNotChangeRoleOrStatus_WhenAnExistingUserIsSavedWithDifferentValues()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-direct");
        await store.SaveAsync(user, CancellationToken.None);

        var loaded = (await store.GetByIdAsync(Tenant, user.UserId, CancellationToken.None))!;
        loaded.Status = UserStatus.Deactivated;
        loaded.Role = UserRole.Admin;
        await store.SaveAsync(loaded, CancellationToken.None);

        var reloaded = (await store.GetByIdAsync(Tenant, user.UserId, CancellationToken.None))!;
        reloaded.Status.Should().Be(UserStatus.Active,
            because: "the Postgres upsert does not write status for an existing row, so neither may this store");
        reloaded.Role.Should().Be(UserRole.Agent);
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreRoleAndStatus_WhenTheUserIsNew()
    {
        var store = new InMemoryUserStore();

        await store.SaveAsync(NewUser("u-new", UserRole.Supervisor, UserStatus.Suspended), CancellationToken.None);

        var stored = (await store.GetByIdAsync(Tenant, EntityId.From("u-new"), CancellationToken.None))!;
        stored.Role.Should().Be(UserRole.Supervisor);
        stored.Status.Should().Be(UserStatus.Suspended);
    }

    [Fact]
    public async Task SaveAsync_ShouldStoreTheNewRoleAndStatus_WhenTheUserWasDeletedFirst()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(NewUser("u-again"), CancellationToken.None);
        await store.DeleteAsync(Tenant, EntityId.From("u-again"), CancellationToken.None);

        await store.SaveAsync(NewUser("u-again", UserRole.Admin, UserStatus.Deactivated), CancellationToken.None);

        var stored = (await store.GetByIdAsync(Tenant, EntityId.From("u-again"), CancellationToken.None))!;
        stored.Role.Should().Be(UserRole.Admin, because: "a deleted user saved again is a new user");
        stored.Status.Should().Be(UserStatus.Deactivated);
    }

    [Fact]
    public async Task SaveAsync_ShouldThrowAndStoreNothing_WhenAnotherUserHasTheEmail()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(NewUser("u-first"), CancellationToken.None);
        var duplicate = NewUser("u-second");
        var sameEmail = new User
        {
            UserId = duplicate.UserId,
            TenantId = duplicate.TenantId,
            Email = "U-FIRST@users.test",
            DisplayName = duplicate.DisplayName,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = duplicate.CreatedAt,
        };

        var save = () => store.SaveAsync(sameEmail, CancellationToken.None);

        (await save.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.ConflictingField.Should().Be("email");
        (await store.GetByIdAsync(Tenant, sameEmail.UserId, CancellationToken.None)).Should().BeNull();
        await store.SaveAsync(NewUser("u-second", UserRole.Supervisor), CancellationToken.None);
        (await store.GetByIdAsync(Tenant, sameEmail.UserId, CancellationToken.None))!.Role.Should().Be(UserRole.Supervisor,
            because: "the refused save recorded nothing, so the next save of that id is an insert");
    }

    // ─── SetStatusAsync / SetRoleAsync ──────────────────────────────────────

    [Fact]
    public async Task SetStatusAsync_ShouldReturnTheStatusItReplaced_WhenTheUserExists()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(NewUser("u-set-status"), CancellationToken.None);

        var previous = await store.SetStatusAsync(
            Tenant, EntityId.From("u-set-status"), UserStatus.Suspended, ChangedAt, CancellationToken.None);

        previous.Should().Be(UserStatus.Active);
        var stored = (await store.GetByIdAsync(Tenant, EntityId.From("u-set-status"), CancellationToken.None))!;
        stored.Status.Should().Be(UserStatus.Suspended);
        stored.UpdatedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task SetStatusAsync_ShouldReturnNullAndCreateNothing_WhenTheUserDoesNotExist()
    {
        var store = new InMemoryUserStore();

        var previous = await store.SetStatusAsync(
            Tenant, EntityId.From("u-missing"), UserStatus.Suspended, ChangedAt, CancellationToken.None);

        previous.Should().BeNull();
        (await store.GetByIdAsync(Tenant, EntityId.From("u-missing"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SetRoleAsync_ShouldReturnTheRoleItReplaced_WhenTheUserExists()
    {
        var store = new InMemoryUserStore();
        await store.SaveAsync(NewUser("u-set-role", UserRole.Admin), CancellationToken.None);

        var previous = await store.SetRoleAsync(
            Tenant, EntityId.From("u-set-role"), UserRole.Agent, ChangedAt, CancellationToken.None);

        previous.Should().Be(UserRole.Admin);
        var stored = (await store.GetByIdAsync(Tenant, EntityId.From("u-set-role"), CancellationToken.None))!;
        stored.Role.Should().Be(UserRole.Agent);
        stored.UpdatedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task SetRoleAsync_ShouldReturnNullAndCreateNothing_WhenTheUserDoesNotExist()
    {
        var store = new InMemoryUserStore();

        var previous = await store.SetRoleAsync(
            Tenant, EntityId.From("u-missing"), UserRole.Admin, ChangedAt, CancellationToken.None);

        previous.Should().BeNull();
        (await store.GetByIdAsync(Tenant, EntityId.From("u-missing"), CancellationToken.None)).Should().BeNull();
    }

    private static User NewUser(string userId, UserRole role = UserRole.Agent, UserStatus status = UserStatus.Active) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = Tenant,
        Email = $"{userId}@users.test",
        DisplayName = userId,
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A separate copy of <paramref name="user"/>, as a fresh read of its row would return.</summary>
    private static User SnapshotOf(User user) => new()
    {
        UserId = user.UserId,
        TenantId = user.TenantId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        Role = user.Role,
        Status = user.Status,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
        PasswordHash = user.PasswordHash,
        FailedLoginAttempts = user.FailedLoginAttempts,
        AuthProvider = user.AuthProvider,
    };
}
