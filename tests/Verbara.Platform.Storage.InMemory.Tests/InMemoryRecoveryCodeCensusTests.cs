using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// The in-memory twin of the recovery-code census (openspec <c>harden-recovery-code-digest</c>, design D6):
/// a user counts once they hold a non-null, non-empty array; a user holding any legacy-family element (BCrypt
/// or salted SHA-256) counts as legacy, the rest as Argon2id. Classification is prefix-only (design D5), so the
/// fixtures are shape-correct literals, not real digests. The Postgres twin is
/// <c>PostgresRecoveryCodeCensusTests</c>.
/// </summary>
public sealed class InMemoryRecoveryCodeCensusTests
{
    private const string Argon2idShaped = "$argon2id$v=19$m=12288,t=3,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGFzaGhhc2g";
    private const string BcryptShaped = "$2a$10$abcdefghijklmnopqrstuuJ0123456789abcdefghijklmnopqrstuv";
    private const string Sha256Shaped = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static readonly TenantId Tenant = new("t-census");

    [Fact]
    public async Task CountAsync_ShouldCountUsersPerFamily_WhenRowsMixFamilies()
    {
        var store = new InMemoryUserStore();
        await store.CreateAsync(NewUser("u-argon-1", [Argon2idShaped, Argon2idShaped]), CancellationToken.None);
        await store.CreateAsync(NewUser("u-argon-2", [Argon2idShaped]), CancellationToken.None);
        await store.CreateAsync(NewUser("u-bcrypt", [BcryptShaped, BcryptShaped]), CancellationToken.None);
        await store.CreateAsync(NewUser("u-mixed", [Argon2idShaped, BcryptShaped, Sha256Shaped]), CancellationToken.None);
        await store.CreateAsync(NewUser("u-sha256", [Sha256Shaped]), CancellationToken.None);
        await store.CreateAsync(NewUser("u-null", null), CancellationToken.None);
        await store.CreateAsync(NewUser("u-empty", []), CancellationToken.None);
        // An abandoned enrollment: codes written while MFA was still off. Never redeemable, never regenerable.
        await store.CreateAsync(NewUser("u-pending", [BcryptShaped], mfaEnabled: false), CancellationToken.None);

        var census = new InMemoryRecoveryCodeCensus(store);
        census.Should().BeAssignableTo<IRecoveryCodeCensus>(
            because: "the metrics refresher resolves the census through the Identity seam");

        var result = await census.CountAsync(CancellationToken.None);

        result.Should().Be(new RecoveryCodeCensus(UsersOnArgon2id: 2, UsersOnLegacy: 3),
            because: "two users are all-Argon2id; the BCrypt, mixed and SHA-256 users hold a legacy element; "
                   + "a null or empty array holds no codes, and a user whose MFA is off (a pending enrollment) "
                   + "holds none that count; neither is counted");
    }

    private static User NewUser(string userId, IReadOnlyList<string>? recoveryCodes, bool? mfaEnabled = null) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = Tenant,
        Email = $"{userId}@census.test",
        DisplayName = userId,
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        MfaEnabled = mfaEnabled ?? recoveryCodes is { Count: > 0 },
        MfaRecoveryCodes = recoveryCodes,
    };
}
