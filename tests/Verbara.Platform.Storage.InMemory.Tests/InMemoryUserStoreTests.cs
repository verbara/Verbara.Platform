using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// The <see cref="IUserStore"/> write contract, held by the in-memory store exactly as by
/// <c>PostgresUserStore</c>: only <see cref="IUserStore.CreateAsync"/> adds a user; every other write
/// changes an existing user only (creating nothing), writes only its own fields and checks its
/// precondition in the same step; and users go in and come out as copies, as a database read does.
/// </summary>
public sealed class InMemoryUserStoreTests
{
    private static readonly TenantId Tenant = new("t-users");
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // ─── CreateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldStoreEveryField_WhenTheUserIsNew()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-new", UserRole.Supervisor, UserStatus.Suspended);
        user.MfaEnabled = true;
        user.MfaSecret = "SECRET";
        user.MfaRecoveryCodes = ["d1", "d2"];

        await store.CreateAsync(user, CancellationToken.None);

        var stored = await GetAsync(store, "u-new");
        stored.Should().BeEquivalentTo(user);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowAndStoreNothing_WhenTheIdIsTaken()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-taken"), CancellationToken.None);
        var again = NewUser("u-taken", UserRole.Admin);
        var otherEmail = new User
        {
            UserId = again.UserId,
            TenantId = again.TenantId,
            Email = "someone-else@users.test",
            DisplayName = again.DisplayName,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = again.CreatedAt,
        };

        var create = () => store.CreateAsync(otherEmail, CancellationToken.None);

        (await create.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.ConflictingField.Should().BeNull();
        (await GetAsync(store, "u-taken")).Role.Should().Be(UserRole.Agent, because: "an insert never replaces a user");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowWithTheEmailField_WhenAnotherUserHasTheEmail()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-first"), CancellationToken.None);
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

        var create = () => store.CreateAsync(sameEmail, CancellationToken.None);

        (await create.Should().ThrowAsync<EntityAlreadyExistsException>()).Which.ConflictingField.Should().Be("email");
        (await store.GetByIdAsync(Tenant, sameEmail.UserId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldStoreACopy_WhenTheCallerChangesItsObjectAfterwards()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-copy-in");
        await store.CreateAsync(user, CancellationToken.None);

        user.Role = UserRole.Admin;
        user.PasswordHash = "changed-in-memory";

        var stored = await GetAsync(store, "u-copy-in");
        stored.Role.Should().Be(UserRole.Agent);
        stored.PasswordHash.Should().BeNull();
    }

    // ─── Reads hand out copies ───────────────────────────────────────────────

    [Fact]
    public async Task Reads_ShouldHandOutCopies_WhenACallerChangesTheObjectItRead()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-copy-out");
        user.MfaRecoveryCodes = ["d1"];
        await store.CreateAsync(user, CancellationToken.None);

        var byId = await GetAsync(store, "u-copy-out");
        byId.Status = UserStatus.Deactivated;
        byId.FailedLoginAttempts = 9;
        var byEmail = (await store.GetByEmailAsync(Tenant, user.Email, CancellationToken.None))!;
        byEmail.PasswordHash = "changed";
        var listed = (await store.ListAsync(Tenant, new PagedQuery(1, 10), CancellationToken.None)).Items.Single();
        listed.Role = UserRole.Admin;
        var byIds = (await store.GetByIdsAsync(Tenant.Value, [user.UserId.Value], CancellationToken.None)).Single();
        byIds.MfaEnabled = true;

        var stored = await GetAsync(store, "u-copy-out");
        stored.Should().BeEquivalentTo(user, because: "a change to a read copy is not a write");
        stored.Should().NotBeSameAs(byId);
    }

    // ─── Writes never create ─────────────────────────────────────────────────

    public static TheoryData<string> Writes => new()
    {
        nameof(IUserStore.UpdateProfileAsync),
        nameof(IUserStore.UpdateAdminFieldsAsync),
        nameof(IUserStore.SetPasswordHashAsync),
        nameof(IUserStore.RehashPasswordAsync),
        nameof(IUserStore.RecordFailedSignInAsync),
        nameof(IUserStore.ResetLockoutAsync),
        nameof(IUserStore.SetLastLoginAtAsync),
        nameof(IUserStore.SetPendingMfaAsync),
        nameof(IUserStore.EnableMfaAsync),
        nameof(IUserStore.ClearMfaAsync),
        nameof(IUserStore.SetRecoveryCodesAsync),
        nameof(IUserStore.ConsumeRecoveryCodeAsync),
        nameof(IUserStore.DeleteAsync),
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ShouldReportNothingWrittenAndCreateNothing_WhenTheUserWasDeleted(string write)
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-deleted", UserRole.Admin), CancellationToken.None);
        await store.DeleteAsync(Tenant, EntityId.From("u-deleted"), CancellationToken.None);

        var wrote = await WriteAsync(store, write, EntityId.From("u-deleted"));

        wrote.Should().BeFalse(because: "there is no user to write to");
        (await store.GetByIdAsync(Tenant, EntityId.From("u-deleted"), CancellationToken.None)).Should().BeNull(
            because: "only CreateAsync adds a user; no write may bring a deleted one back");
    }

    // ─── UpdateAdminFieldsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnTheReplacedValuesAndTheStoredUser_WhenItWrites()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-admin-fields", UserRole.Admin), CancellationToken.None);

        var result = await store.UpdateAdminFieldsAsync(
            Tenant, EntityId.From("u-admin-fields"),
            new AdminFieldsChange { Role = UserRole.Agent, Status = UserStatus.Suspended },
            ChangedAt, "admin-1", CancellationToken.None);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        result.Previous.Should().Be(new AdminFields("u-admin-fields", UserRole.Admin, UserStatus.Active));
        result.User!.Role.Should().Be(UserRole.Agent);
        result.User.Status.Should().Be(UserStatus.Suspended);
        var stored = await GetAsync(store, "u-admin-fields");
        stored.DisplayName.Should().Be("u-admin-fields", because: "a field left null is not written");
        stored.UpdatedAt.Should().Be(ChangedAt);
        stored.UpdatedBy.Should().Be("admin-1");
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldReturnStaleAndWriteNothing_WhenTheRowNoLongerMatchesTheExpectation()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-stale", UserRole.Admin), CancellationToken.None);
        var seen = new AdminFields("u-stale", UserRole.Admin, UserStatus.Active);
        await store.UpdateAdminFieldsAsync(
            Tenant, EntityId.From("u-stale"), new AdminFieldsChange { Role = UserRole.Agent }, ChangedAt, null, CancellationToken.None);

        var result = await store.UpdateAdminFieldsAsync(
            Tenant, EntityId.From("u-stale"),
            new AdminFieldsChange { DisplayName = "From a stale form", Role = UserRole.Admin, Expected = seen },
            ChangedAt, null, CancellationToken.None);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Stale);
        var stored = await GetAsync(store, "u-stale");
        stored.Role.Should().Be(UserRole.Agent, because: "a change based on what the row used to hold is refused");
        stored.DisplayName.Should().Be("u-stale");
    }

    [Fact]
    public async Task UpdateAdminFieldsAsync_ShouldWrite_WhenTheRowStillMatchesTheExpectation()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-current"), CancellationToken.None);

        var result = await store.UpdateAdminFieldsAsync(
            Tenant, EntityId.From("u-current"),
            new AdminFieldsChange { DisplayName = "Renamed", Expected = new AdminFields("u-current", UserRole.Agent, UserStatus.Active) },
            ChangedAt, null, CancellationToken.None);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        (await GetAsync(store, "u-current")).DisplayName.Should().Be("Renamed");
    }

    // ─── Password ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetPasswordHashAsync_ShouldClearTheLockout_WhenAsked()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(LockedUser("u-reset"), CancellationToken.None);

        var wrote = await store.SetPasswordHashAsync(
            Tenant, EntityId.From("u-reset"), "new-hash", ChangedAt, clearLockout: true, expectedCurrentHash: null, CancellationToken.None);

        wrote.Should().BeTrue();
        var stored = await GetAsync(store, "u-reset");
        stored.PasswordHash.Should().Be("new-hash");
        stored.PasswordChangedAt.Should().Be(ChangedAt);
        stored.FailedLoginAttempts.Should().Be(0);
        stored.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task SetPasswordHashAsync_ShouldWriteNothing_WhenTheExpectedHashIsNoLongerStored()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-cas");
        user.PasswordHash = "hash-1";
        await store.CreateAsync(user, CancellationToken.None);
        await store.SetPasswordHashAsync(Tenant, user.UserId, "hash-2", ChangedAt, false, null, CancellationToken.None);

        var wrote = await store.SetPasswordHashAsync(
            Tenant, user.UserId, "hash-3", ChangedAt, clearLockout: false, expectedCurrentHash: "hash-1", CancellationToken.None);

        wrote.Should().BeFalse();
        (await GetAsync(store, "u-cas")).PasswordHash.Should().Be("hash-2");
    }

    [Fact]
    public async Task RehashPasswordAsync_ShouldWriteNothing_WhenThePasswordChangedSinceTheLogin()
    {
        var store = new InMemoryUserStore();
        var user = NewUser("u-rehash");
        user.PasswordHash = "bcrypt-of-old";
        await store.CreateAsync(user, CancellationToken.None);
        await store.SetPasswordHashAsync(Tenant, user.UserId, "argon-of-new", ChangedAt, false, null, CancellationToken.None);

        var wrote = await store.RehashPasswordAsync(Tenant, user.UserId, "bcrypt-of-old", "argon-of-old", CancellationToken.None);

        wrote.Should().BeFalse();
        (await GetAsync(store, "u-rehash")).PasswordHash.Should().Be("argon-of-new");
    }

    // ─── Failed sign-ins and the lock ────────────────────────────────────────

    [Fact]
    public async Task RecordFailedSignInAsync_ShouldCountEveryAttemptAndLockAtTheThreshold_WhenCalledInParallel()
    {
        const int attempts = 100;
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-guessed"), CancellationToken.None);
        var lockUntil = DateTimeOffset.UtcNow.AddMinutes(15);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ => Task.Run(() =>
            store.RecordFailedSignInAsync(Tenant, EntityId.From("u-guessed"), 5, lockUntil, CancellationToken.None))));

        results.Select(r => r!.Value.FailedAttempts).Should().BeEquivalentTo(Enumerable.Range(1, attempts),
            because: "each attempt is counted exactly once");
        results.Where(r => r!.Value.FailedAttempts < 5).Should().OnlyContain(r => r!.Value.LockedUntil == null);
        var stored = await GetAsync(store, "u-guessed");
        stored.FailedLoginAttempts.Should().Be(attempts);
        stored.LockedUntil.Should().Be(lockUntil);
    }

    [Fact]
    public async Task ResetLockoutAsync_ShouldKeepALockInForce_WhenOnlyIfUnlocked()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(LockedUser("u-locked"), CancellationToken.None);

        var wrote = await store.ResetLockoutAsync(
            Tenant, EntityId.From("u-locked"), DateTimeOffset.UtcNow, onlyIfUnlocked: true, CancellationToken.None);

        wrote.Should().BeFalse();
        (await GetAsync(store, "u-locked")).IsLockedOut(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task ResetLockoutAsync_ShouldClearAnExpiredLock_WhenOnlyIfUnlocked()
    {
        var store = new InMemoryUserStore();
        var user = LockedUser("u-expired");
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.CreateAsync(user, CancellationToken.None);

        var wrote = await store.ResetLockoutAsync(
            Tenant, user.UserId, DateTimeOffset.UtcNow, onlyIfUnlocked: true, CancellationToken.None);

        wrote.Should().BeTrue();
        var stored = await GetAsync(store, "u-expired");
        stored.FailedLoginAttempts.Should().Be(0);
        stored.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task ResetLockoutAsync_ShouldLiftALockInForce_WhenNotOnlyIfUnlocked()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(LockedUser("u-unlock"), CancellationToken.None);

        var wrote = await store.ResetLockoutAsync(
            Tenant, EntityId.From("u-unlock"), DateTimeOffset.UtcNow, onlyIfUnlocked: false, CancellationToken.None);

        wrote.Should().BeTrue();
        (await GetAsync(store, "u-unlock")).LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task SetLastLoginAtAsync_ShouldNeverMoveTheTimeBack_WhenAnOlderTimeArrivesLast()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-last-login"), CancellationToken.None);

        await store.SetLastLoginAtAsync(Tenant, EntityId.From("u-last-login"), ChangedAt, CancellationToken.None);
        await store.SetLastLoginAtAsync(Tenant, EntityId.From("u-last-login"), ChangedAt.AddMinutes(-5), CancellationToken.None);

        (await GetAsync(store, "u-last-login")).LastLoginAt.Should().Be(ChangedAt);
    }

    // ─── MFA ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetPendingMfaAsync_ShouldWriteNothing_WhenMfaIsEnabled()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(MfaUser("u-enrolled"), CancellationToken.None);

        var wrote = await store.SetPendingMfaAsync(
            Tenant, EntityId.From("u-enrolled"), "ATTACKER", ["attacker-digest"], ChangedAt, CancellationToken.None);

        wrote.Should().BeFalse(because: "an enrolled factor is replaced only after it is disabled");
        var stored = await GetAsync(store, "u-enrolled");
        stored.MfaSecret.Should().Be("ENROLLED");
        stored.MfaRecoveryCodes.Should().Equal("enrolled-digest");
    }

    [Fact]
    public async Task EnableMfaAsync_ShouldEnableOnce_WhenASecretIsPending()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-enable"), CancellationToken.None);
        (await store.EnableMfaAsync(Tenant, EntityId.From("u-enable"), ChangedAt, CancellationToken.None))
            .Should().BeFalse(because: "there is no pending secret to confirm");
        await store.SetPendingMfaAsync(Tenant, EntityId.From("u-enable"), "PENDING", ["d"], ChangedAt, CancellationToken.None);

        var first = await store.EnableMfaAsync(Tenant, EntityId.From("u-enable"), ChangedAt, CancellationToken.None);
        var second = await store.EnableMfaAsync(Tenant, EntityId.From("u-enable"), ChangedAt.AddMinutes(1), CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse(because: "an enabled factor is not confirmed again");
        var stored = await GetAsync(store, "u-enable");
        stored.MfaEnabled.Should().BeTrue();
        stored.MfaConfirmedAt.Should().Be(ChangedAt);
    }

    [Fact]
    public async Task ClearMfaAsync_ShouldClearTheFactorAndTheLockout_WhenAsked()
    {
        var store = new InMemoryUserStore();
        var user = MfaUser("u-clear");
        user.FailedLoginAttempts = 5;
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        await store.CreateAsync(user, CancellationToken.None);

        var wrote = await store.ClearMfaAsync(Tenant, user.UserId, clearLockout: true, ChangedAt, CancellationToken.None);

        wrote.Should().BeTrue();
        var stored = await GetAsync(store, "u-clear");
        stored.MfaEnabled.Should().BeFalse();
        stored.MfaSecret.Should().BeNull();
        stored.MfaRecoveryCodes.Should().BeNull();
        stored.MfaConfirmedAt.Should().BeNull();
        stored.FailedLoginAttempts.Should().Be(0);
        stored.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task SetRecoveryCodesAsync_ShouldWriteNothing_WhenMfaIsOff()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-no-mfa"), CancellationToken.None);

        var wrote = await store.SetRecoveryCodesAsync(Tenant, EntityId.From("u-no-mfa"), ["d"], ChangedAt, CancellationToken.None);

        wrote.Should().BeFalse();
        (await GetAsync(store, "u-no-mfa")).MfaRecoveryCodes.Should().BeNull();
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ShouldSucceedForExactlyOneRedemption_WhenOneCodeIsRedeemedInParallel()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(MfaUser("u-redeem"), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            store.ConsumeRecoveryCodeAsync(Tenant, EntityId.From("u-redeem"), "enrolled-digest", CancellationToken.None))));

        results.Count(r => r).Should().Be(1, because: "a recovery code is single-use");
        (await GetAsync(store, "u-redeem")).MfaRecoveryCodes.Should().BeEmpty();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<bool> WriteAsync(InMemoryUserStore store, string write, EntityId id)
    {
        var ct = CancellationToken.None;
        return write switch
        {
            nameof(IUserStore.UpdateProfileAsync) => await store.UpdateProfileAsync(Tenant, id, new UserProfileChange { DisplayName = "x" }, ChangedAt, ct),
            nameof(IUserStore.UpdateAdminFieldsAsync) => (await store.UpdateAdminFieldsAsync(Tenant, id, new AdminFieldsChange { Role = UserRole.Agent }, ChangedAt, null, ct)).Outcome == AdminFieldsWriteOutcome.Written,
            nameof(IUserStore.SetPasswordHashAsync) => await store.SetPasswordHashAsync(Tenant, id, "h", ChangedAt, true, null, ct),
            nameof(IUserStore.RehashPasswordAsync) => await store.RehashPasswordAsync(Tenant, id, "old", "new", ct),
            nameof(IUserStore.RecordFailedSignInAsync) => await store.RecordFailedSignInAsync(Tenant, id, 5, ChangedAt, ct) is not null,
            nameof(IUserStore.ResetLockoutAsync) => await store.ResetLockoutAsync(Tenant, id, ChangedAt, false, ct),
            nameof(IUserStore.SetLastLoginAtAsync) => await store.SetLastLoginAtAsync(Tenant, id, ChangedAt, ct),
            nameof(IUserStore.SetPendingMfaAsync) => await store.SetPendingMfaAsync(Tenant, id, "S", ["d"], ChangedAt, ct),
            nameof(IUserStore.EnableMfaAsync) => await store.EnableMfaAsync(Tenant, id, ChangedAt, ct),
            nameof(IUserStore.ClearMfaAsync) => await store.ClearMfaAsync(Tenant, id, true, ChangedAt, ct),
            nameof(IUserStore.SetRecoveryCodesAsync) => await store.SetRecoveryCodesAsync(Tenant, id, ["d"], ChangedAt, ct),
            nameof(IUserStore.ConsumeRecoveryCodeAsync) => await store.ConsumeRecoveryCodeAsync(Tenant, id, "d", ct),
            nameof(IUserStore.DeleteAsync) => await store.DeleteAsync(Tenant, id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "not a user-store write"),
        };
    }

    private static async Task<User> GetAsync(InMemoryUserStore store, string userId) =>
        (await store.GetByIdAsync(Tenant, EntityId.From(userId), CancellationToken.None))!;

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

    private static User LockedUser(string userId)
    {
        var user = NewUser(userId);
        user.FailedLoginAttempts = 5;
        user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        return user;
    }

    private static User MfaUser(string userId)
    {
        var user = NewUser(userId);
        user.MfaEnabled = true;
        user.MfaSecret = "ENROLLED";
        user.MfaRecoveryCodes = ["enrolled-digest"];
        user.MfaConfirmedAt = ChangedAt.AddDays(-30);
        return user;
    }
}
