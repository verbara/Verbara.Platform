using FluentAssertions.Equivalency;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Writers that race on one <c>users</c> row, against a real Postgres. Every write except an admin's
/// change of role or status starts from a copy of the row read earlier — a failed sign-in, a password
/// change, the deferred last-login flush. None of them may bring a deleted row back, and none may put
/// back the password, MFA state, lock or failed-attempt count another write replaced in between:
/// each writer is one UPDATE of its own columns, with its precondition in the same statement.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresUserStoreConcurrencyTests
    : IClassFixture<UserMfaEncryptionFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly UserMfaEncryptionFixture _fixture;
    private readonly PostgresUserStore _sut;
    private readonly TenantId _tenant;

    public PostgresUserStoreConcurrencyTests(UserMfaEncryptionFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresUserStore(_fixture.DataSource, _fixture.DataProtection);
        _tenant = new TenantId($"t-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedTenantAsync(_tenant.Value);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    // ─── A deleted account stays deleted ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ShouldCreateNothing_WhenTheUserWasDemotedSuspendedAndDeleted(string write)
    {
        var id = EntityId.From("u-deleted");
        await _sut.CreateAsync(NewMfaUser(id, UserRole.Admin), default);
        await _sut.UpdateAdminFieldsAsync(
            _tenant, id, new AdminFieldsChange { Role = UserRole.Agent, Status = UserStatus.Suspended }, ChangedAt, null, default);
        await _sut.DeleteAsync(_tenant, id, default);

        var wrote = await WriteAsync(write, id);

        wrote.Should().BeFalse(because: "there is no row to write to");
        (await _sut.GetByIdAsync(_tenant, id, default)).Should().BeNull(
            because: "only CreateAsync inserts; a write from a copy read before the delete must not bring the row back");
    }

    // ─── Each writer changes only its own columns ────────────────────────────

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task Write_ShouldChangeOnlyItsOwnColumns_WhenItWrites(string write)
    {
        if (write == nameof(IUserStore.DeleteAsync))
            return; // removes the row; covered above

        var id = EntityId.From("u-columns");
        var pendingEnrollment = write is nameof(IUserStore.SetPendingMfaAsync) or nameof(IUserStore.EnableMfaAsync);
        await _sut.CreateAsync(FullyPopulatedUser(id, mfaEnabled: !pendingEnrollment), default);
        var before = (await _sut.GetByIdAsync(_tenant, id, default))!;

        var firstDigest = before.MfaRecoveryCodes is { Count: > 0 } codes ? codes[0] : null;
        (await WriteAsync(write, id, digest: firstDigest)).Should().BeTrue();

        var after = (await _sut.GetByIdAsync(_tenant, id, default))!;
        var own = OwnColumns[write];
        after.Should().BeEquivalentTo(before, options => options.Excluding((IMemberInfo member) => own.Contains(member.Name)),
            because: $"{write} writes only {string.Join(", ", own)}");
    }

    // ─── Stale copies cannot put a column back ───────────────────────────────

    [Fact]
    public async Task SetPasswordHashAsync_ShouldWriteNothing_WhenTheVerifiedHashWasReplacedMeanwhile()
    {
        var id = EntityId.From("u-password");
        await _sut.CreateAsync(NewUser(id, passwordHash: "old-hash"), default);
        var readBeforeTheReset = (await _sut.GetByIdAsync(_tenant, id, default))!;
        await _sut.SetPasswordHashAsync(_tenant, id, "reset-hash", ChangedAt, clearLockout: true, expectedCurrentHash: null, default);

        var wrote = await _sut.SetPasswordHashAsync(
            _tenant, id, "changed-hash", ChangedAt, clearLockout: false, expectedCurrentHash: readBeforeTheReset.PasswordHash, default);

        wrote.Should().BeFalse(because: "the change was proven with a password that is no longer the account's");
        (await _sut.GetByIdAsync(_tenant, id, default))!.PasswordHash.Should().Be("reset-hash");
    }

    [Fact]
    public async Task RehashPasswordAsync_ShouldKeepTheChangedPassword_WhenADeferredRehashOfTheOldPasswordLandsAfterIt()
    {
        var id = EntityId.From("u-rehash");
        await _sut.CreateAsync(NewUser(id, passwordHash: "bcrypt-of-old-password"), default);
        var verifiedAtLogin = (await _sut.GetByIdAsync(_tenant, id, default))!.PasswordHash!;
        await _sut.SetPasswordHashAsync(_tenant, id, "argon2-of-new-password", ChangedAt, false, null, default);

        var wrote = await _sut.RehashPasswordAsync(_tenant, id, verifiedAtLogin, "argon2-of-old-password", default);

        wrote.Should().BeFalse();
        (await _sut.GetByIdAsync(_tenant, id, default))!.PasswordHash.Should().Be("argon2-of-new-password",
            because: "re-hashing the old password must not undo a later password change");
    }

    [Fact]
    public async Task RehashPasswordAsync_ShouldReplaceTheHash_WhenTheVerifiedHashIsStillStored()
    {
        var id = EntityId.From("u-rehash-ok");
        await _sut.CreateAsync(NewUser(id, passwordHash: "bcrypt"), default);

        (await _sut.RehashPasswordAsync(_tenant, id, "bcrypt", "argon2", default)).Should().BeTrue();

        (await _sut.GetByIdAsync(_tenant, id, default))!.PasswordHash.Should().Be("argon2");
    }

    [Fact]
    public async Task ClearMfaAsync_ShouldKeepTheAdminUnlockAndTheClearedFactor_WhenAStaleSignInWriteFollows()
    {
        var id = EntityId.From("u-unlock");
        var locked = NewMfaUser(id);
        locked.FailedLoginAttempts = 5;
        locked.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        await _sut.CreateAsync(locked, default);

        (await _sut.ClearMfaAsync(_tenant, id, clearLockout: true, ChangedAt, default)).Should().BeTrue();
        // What a sign-in that read the account before the reset writes afterwards.
        await _sut.SetLastLoginAtAsync(_tenant, id, ChangedAt, default);
        await _sut.ResetLockoutAsync(_tenant, id, DateTimeOffset.UtcNow, onlyIfUnlocked: true, default);

        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.LockedUntil.Should().BeNull(because: "the admin's unlock must not be undone");
        stored.FailedLoginAttempts.Should().Be(0);
        stored.MfaEnabled.Should().BeFalse();
        stored.MfaSecret.Should().BeNull();
        stored.MfaRecoveryCodes.Should().BeNull();
    }

    [Fact]
    public async Task SetPendingMfaAsync_ShouldNotReplaceAnEnrolledFactor_WhenMfaIsEnabled()
    {
        var id = EntityId.From("u-enrolled");
        await _sut.CreateAsync(NewMfaUser(id), default);

        var wrote = await _sut.SetPendingMfaAsync(_tenant, id, "ATTACKERSECRET", ["attacker-digest"], ChangedAt, default);

        wrote.Should().BeFalse();
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.MfaSecret.Should().Be("JBSWY3DPEHPK3PXP");
        stored.MfaRecoveryCodes.Should().Equal("digest-1", "digest-2");
    }

    // ─── The failed-attempt counter ──────────────────────────────────────────

    [Fact]
    public async Task RecordFailedSignInAsync_ShouldCountBothAttempts_WhenTwoSnapshotsEachRecordOne()
    {
        var id = EntityId.From("u-attempts");
        await _sut.CreateAsync(NewUser(id), default);
        var lockUntil = DateTimeOffset.UtcNow.AddMinutes(15);

        var first = await _sut.RecordFailedSignInAsync(_tenant, id, 5, lockUntil, default);
        var second = await _sut.RecordFailedSignInAsync(_tenant, id, 5, lockUntil, default);

        first!.Value.FailedAttempts.Should().Be(1);
        second!.Value.FailedAttempts.Should().Be(2);
        (await _sut.GetByIdAsync(_tenant, id, default))!.FailedLoginAttempts.Should().Be(2);
    }

    [Fact]
    public async Task RecordFailedSignInAsync_ShouldCountEveryAttemptAndLockAtTheThreshold_WhenAttemptsArriveInParallel()
    {
        const int attempts = 20;
        const int threshold = 5;
        var id = EntityId.From("u-parallel");
        await _sut.CreateAsync(NewUser(id), default);
        var lockUntil = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var results = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            _sut.RecordFailedSignInAsync(_tenant, id, threshold, lockUntil, default)));

        results.Select(r => r!.Value.FailedAttempts).Should().BeEquivalentTo(Enumerable.Range(1, attempts),
            because: "each concurrent attempt is counted exactly once");
        results.Where(r => r!.Value.FailedAttempts < threshold).Should().OnlyContain(r => r!.Value.LockedUntil == null,
            because: "the lock is set by the attempt that reaches the threshold, not before");
        results.Where(r => r!.Value.FailedAttempts >= threshold).Should().OnlyContain(r => r!.Value.LockedUntil == lockUntil);
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.FailedLoginAttempts.Should().Be(attempts);
        stored.LockedUntil.Should().Be(lockUntil);
    }

    [Fact]
    public async Task ResetLockoutAsync_ShouldKeepALockSetAfterTheSuccess_WhenOnlyIfUnlocked()
    {
        var id = EntityId.From("u-reset-after-lock");
        await _sut.CreateAsync(NewUser(id), default);
        for (var i = 0; i < 5; i++)
            await _sut.RecordFailedSignInAsync(_tenant, id, 5, DateTimeOffset.UtcNow.AddMinutes(15), default);

        var wrote = await _sut.ResetLockoutAsync(_tenant, id, DateTimeOffset.UtcNow, onlyIfUnlocked: true, default);

        wrote.Should().BeFalse();
        var stored = (await _sut.GetByIdAsync(_tenant, id, default))!;
        stored.IsLockedOut(DateTimeOffset.UtcNow).Should().BeTrue();
        stored.FailedLoginAttempts.Should().Be(5);
    }

    [Fact]
    public async Task SetLastLoginAtAsync_ShouldNeverMoveTheTimeBack_WhenAnOlderTimeArrivesLast()
    {
        var id = EntityId.From("u-last-login");
        await _sut.CreateAsync(NewUser(id), default);

        await _sut.SetLastLoginAtAsync(_tenant, id, ChangedAt, default);
        await _sut.SetLastLoginAtAsync(_tenant, id, ChangedAt.AddMinutes(-5), default);

        (await _sut.GetByIdAsync(_tenant, id, default))!.LastLoginAt.Should().Be(ChangedAt);
    }

    // ─── Recovery codes are single-use ───────────────────────────────────────

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ShouldSucceedForExactlyOneRedemption_WhenOneCodeIsRedeemedConcurrently()
    {
        var id = EntityId.From("u-redeem");
        await _sut.CreateAsync(NewMfaUser(id), default);
        var digest = (await _sut.GetByIdAsync(_tenant, id, default))!.MfaRecoveryCodes![0];

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _sut.ConsumeRecoveryCodeAsync(_tenant, id, digest, default)));

        results.Count(r => r).Should().Be(1, because: "a recovery code redeems once, however many requests race on it");
        (await _sut.GetByIdAsync(_tenant, id, default))!.MfaRecoveryCodes.Should().Equal("digest-2");
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ShouldRemoveALegacyElement_WhenTheMigratorHasNotWrappedItYet()
    {
        var id = EntityId.From("u-legacy");
        await _fixture.WriteRawMfaMaterialAsync(_tenant.Value, id.Value, mfaSecret: "JBSWY3DPEHPK3PXP", ["legacy-1", "legacy-2"]);

        (await _sut.ConsumeRecoveryCodeAsync(_tenant, id, "legacy-1", default)).Should().BeTrue();
        (await _sut.ConsumeRecoveryCodeAsync(_tenant, id, "legacy-1", default)).Should().BeFalse();

        (await _fixture.ReadRawRecoveryCodesAsync(_tenant.Value, id.Value)).Should().Equal("legacy-2");
    }

    [Fact]
    public async Task ConsumeRecoveryCodeAsync_ShouldRemoveNothing_WhenTheCodeIsNotStored()
    {
        var id = EntityId.From("u-unknown-code");
        await _sut.CreateAsync(NewMfaUser(id), default);

        (await _sut.ConsumeRecoveryCodeAsync(_tenant, id, "not-a-stored-digest", default)).Should().BeFalse();

        (await _sut.GetByIdAsync(_tenant, id, default))!.MfaRecoveryCodes.Should().Equal("digest-1", "digest-2");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // The columns each writer may change; every other column must come back exactly as it was.
    private static readonly Dictionary<string, string[]> OwnColumns = new(StringComparer.Ordinal)
    {
        [nameof(IUserStore.UpdateProfileAsync)] = [nameof(User.DisplayName), nameof(User.EmailVerified), nameof(User.AuthProvider), nameof(User.OidcSubject), nameof(User.UpdatedAt)],
        // CanAuthenticate is derived from Status.
        [nameof(IUserStore.UpdateAdminFieldsAsync)] = [nameof(User.DisplayName), nameof(User.Role), nameof(User.Status), nameof(User.CanAuthenticate), nameof(User.UpdatedAt), nameof(User.UpdatedBy)],
        [nameof(IUserStore.SetPasswordHashAsync)] = [nameof(User.PasswordHash), nameof(User.PasswordChangedAt), nameof(User.FailedLoginAttempts), nameof(User.LockedUntil)],
        [nameof(IUserStore.RehashPasswordAsync)] = [nameof(User.PasswordHash)],
        [nameof(IUserStore.RecordFailedSignInAsync)] = [nameof(User.FailedLoginAttempts), nameof(User.LockedUntil)],
        [nameof(IUserStore.ResetLockoutAsync)] = [nameof(User.FailedLoginAttempts), nameof(User.LockedUntil)],
        [nameof(IUserStore.SetLastLoginAtAsync)] = [nameof(User.LastLoginAt)],
        [nameof(IUserStore.SetPendingMfaAsync)] = [nameof(User.MfaSecret), nameof(User.MfaRecoveryCodes), nameof(User.UpdatedAt)],
        [nameof(IUserStore.EnableMfaAsync)] = [nameof(User.MfaEnabled), nameof(User.MfaConfirmedAt), nameof(User.UpdatedAt)],
        [nameof(IUserStore.ClearMfaAsync)] = [nameof(User.MfaEnabled), nameof(User.MfaSecret), nameof(User.MfaRecoveryCodes), nameof(User.MfaConfirmedAt), nameof(User.UpdatedAt), nameof(User.FailedLoginAttempts), nameof(User.LockedUntil)],
        [nameof(IUserStore.SetRecoveryCodesAsync)] = [nameof(User.MfaRecoveryCodes), nameof(User.UpdatedAt)],
        [nameof(IUserStore.ConsumeRecoveryCodeAsync)] = [nameof(User.MfaRecoveryCodes)],
    };

    private async Task<bool> WriteAsync(string write, EntityId id, string? digest = null)
    {
        var at = ChangedAt.AddHours(1);
        return write switch
        {
            nameof(IUserStore.UpdateProfileAsync) => await _sut.UpdateProfileAsync(
                _tenant, id, new UserProfileChange { DisplayName = "IdP name", EmailVerified = true, AuthProvider = "oidc", OidcSubject = "sub-2" }, at, default),
            nameof(IUserStore.UpdateAdminFieldsAsync) => (await _sut.UpdateAdminFieldsAsync(
                _tenant, id, new AdminFieldsChange { DisplayName = "Admin name", Role = UserRole.Supervisor, Status = UserStatus.Deactivated }, at, "admin-2", default)).Outcome == AdminFieldsWriteOutcome.Written,
            nameof(IUserStore.SetPasswordHashAsync) => await _sut.SetPasswordHashAsync(_tenant, id, "set-hash", at, clearLockout: true, expectedCurrentHash: null, default),
            nameof(IUserStore.RehashPasswordAsync) => await _sut.RehashPasswordAsync(_tenant, id, "seed-hash", "rehashed", default),
            nameof(IUserStore.RecordFailedSignInAsync) => await _sut.RecordFailedSignInAsync(_tenant, id, 100, at.AddMinutes(15), default) is not null,
            nameof(IUserStore.ResetLockoutAsync) => await _sut.ResetLockoutAsync(_tenant, id, at, onlyIfUnlocked: false, default),
            nameof(IUserStore.SetLastLoginAtAsync) => await _sut.SetLastLoginAtAsync(_tenant, id, at.AddYears(1), default),
            nameof(IUserStore.SetPendingMfaAsync) => await _sut.SetPendingMfaAsync(_tenant, id, "NEWPENDINGSECRET", ["new-digest"], at, default),
            nameof(IUserStore.EnableMfaAsync) => await _sut.EnableMfaAsync(_tenant, id, at, default),
            nameof(IUserStore.ClearMfaAsync) => await _sut.ClearMfaAsync(_tenant, id, clearLockout: true, at, default),
            nameof(IUserStore.SetRecoveryCodesAsync) => await _sut.SetRecoveryCodesAsync(_tenant, id, ["new-digest"], at, default),
            nameof(IUserStore.ConsumeRecoveryCodeAsync) => await _sut.ConsumeRecoveryCodeAsync(_tenant, id, digest ?? "digest-1", default),
            nameof(IUserStore.DeleteAsync) => await _sut.DeleteAsync(_tenant, id, default),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "not a user-store write"),
        };
    }

    private User NewUser(EntityId id, UserRole role = UserRole.Agent, string? passwordHash = null) => new()
    {
        UserId = id,
        TenantId = _tenant,
        Email = $"{id.Value}@users.test",
        DisplayName = id.Value,
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        PasswordHash = passwordHash,
    };

    private User NewMfaUser(EntityId id, UserRole role = UserRole.Agent)
    {
        var user = NewUser(id, role, passwordHash: "hash");
        user.MfaEnabled = true;
        user.MfaSecret = "JBSWY3DPEHPK3PXP";
        user.MfaRecoveryCodes = ["digest-1", "digest-2"];
        user.MfaConfirmedAt = ChangedAt.AddDays(-30);
        return user;
    }

    // Every column holds a value distinct from what any writer would write, so a writer touching a
    // column it does not own shows up in the comparison.
    private User FullyPopulatedUser(EntityId id, bool mfaEnabled) => new()
    {
        UserId = id,
        TenantId = _tenant,
        Email = $"{id.Value}@users.test",
        DisplayName = "Seed name",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = ChangedAt.AddDays(-100),
        UpdatedAt = ChangedAt.AddDays(-10),
        CreatedBy = "seed-creator",
        UpdatedBy = "seed-updater",
        PasswordHash = "seed-hash",
        MfaEnabled = mfaEnabled,
        MfaSecret = "SEEDSECRETBASE32",
        MfaRecoveryCodes = ["seed-digest-1", "seed-digest-2"],
        MfaConfirmedAt = mfaEnabled ? ChangedAt.AddDays(-20) : null,
        EmailVerified = false,
        FailedLoginAttempts = 3,
        LockedUntil = ChangedAt.AddDays(-1),
        PasswordChangedAt = ChangedAt.AddDays(-30),
        LastLoginAt = ChangedAt.AddDays(-2),
        AuthProvider = "local",
        ExternalId = "ext-1",
        OidcSubject = "sub-1",
    };
}
