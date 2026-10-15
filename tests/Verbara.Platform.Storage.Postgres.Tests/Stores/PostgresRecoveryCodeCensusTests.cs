using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Live-DB twin of <c>InMemoryRecoveryCodeCensusTests</c> (openspec <c>harden-recovery-code-digest</c>,
/// design D6). <c>users.mfa_recovery_codes</c> is DataProtection-wrapped per element, so SQL cannot see a
/// digest's family: the census must unwrap each row the way <see cref="PostgresUserStore"/> does, including a
/// row the encryption migrator has not reached yet (stored unwrapped, which must be classified as itself).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresRecoveryCodeCensusTests
    : IClassFixture<UserMfaEncryptionFixture>, IAsyncLifetime
{
    private const string Argon2idShaped = "$argon2id$v=19$m=12288,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g";
    private const string BcryptShaped = "$2a$10$abcdefghijklmnopqrstuuJ0123456789abcdefghijklmnopqrstuv";
    private const string Sha256Shaped = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private readonly UserMfaEncryptionFixture _fixture;
    private readonly PostgresUserStore _store;
    private readonly string _tenantId;

    public PostgresRecoveryCodeCensusTests(UserMfaEncryptionFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresUserStore(_fixture.DataSource, _fixture.DataProtection);
        _tenantId = $"t-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedTenantAsync(_tenantId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CountAsync_ShouldCountUsersPerFamily_WhenRowsMixFamilies()
    {
        // Wrapped through the store, as every write since encrypt-mfa-secrets-at-rest.
        await _store.CreateAsync(NewUser("u-argon-1", [Argon2idShaped, Argon2idShaped]), default);
        await _store.CreateAsync(NewUser("u-argon-2", [Argon2idShaped]), default);
        await _store.CreateAsync(NewUser("u-bcrypt", [BcryptShaped, BcryptShaped]), default);
        await _store.CreateAsync(NewUser("u-mixed", [Argon2idShaped, BcryptShaped, Sha256Shaped]), default);
        await _store.CreateAsync(NewUser("u-null", null), default);
        await _store.CreateAsync(NewUser("u-empty", []), default);
        // An abandoned enrollment: codes written while MFA was still off. Never redeemable, never regenerable.
        await _store.CreateAsync(NewUser("u-pending", [BcryptShaped], mfaEnabled: false), default);

        // Still unwrapped: the migrator has not run over this row.
        await _fixture.WriteRawMfaMaterialAsync(_tenantId, "u-sha256-unwrapped", mfaSecret: null, [Sha256Shaped]);

        var census = new PostgresRecoveryCodeCensus(_fixture.DataSource, _fixture.DataProtection);
        census.Should().BeAssignableTo<IRecoveryCodeCensus>(
            because: "the metrics refresher resolves the census through the Identity seam");

        var result = await census.CountAsync(CancellationToken.None);

        result.Should().Be(new RecoveryCodeCensus(UsersOnArgon2id: 2, UsersOnLegacy: 3),
            because: "two users are all-Argon2id; the BCrypt, mixed and unwrapped SHA-256 users hold a legacy "
                   + "element; a null or empty array holds no codes, and a user whose MFA is off (a pending "
                   + "enrollment) holds none that count; neither is counted");
    }

    private User NewUser(string userId, IReadOnlyList<string>? recoveryCodes, bool? mfaEnabled = null) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(_tenantId),
        Email = $"{userId}@census.test",
        DisplayName = "Recovery-code census subject",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        MfaEnabled = mfaEnabled ?? recoveryCodes is { Count: > 0 },
        MfaRecoveryCodes = recoveryCodes,
    };
}
