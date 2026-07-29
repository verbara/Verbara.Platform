using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// A7 (encrypt-mfa-secrets-at-rest) regression suite. Asserts that
/// <see cref="PostgresUserStore"/> never persists MFA enrollment material —
/// the TOTP shared secret or any recovery-code digest — in plaintext, that its
/// <c>UserRow</c> → <c>User</c> projection transparently unwraps stored
/// ciphertext for internal callers (<c>MfaService.VerifyCode</c>,
/// <c>MfaService.ValidateRecoveryCode</c>, <c>MfaAdminService</c>) while
/// falling back verbatim for rows the migrator has not reached yet, and that
/// the <see cref="UserMfaEncryptionMigrator"/> idempotently wraps every legacy
/// row it finds at host startup — including the mixed wrapped/legacy array a
/// crash mid-migration leaves behind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresUserStoreMfaEncryptionTests
    : IClassFixture<UserMfaEncryptionFixture>, IAsyncLifetime
{
    private readonly UserMfaEncryptionFixture _fixture;
    private readonly PostgresUserStore _sut;
    private readonly string _tenantId;

    public PostgresUserStoreMfaEncryptionTests(UserMfaEncryptionFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresUserStore(_fixture.DataSource, _fixture.DataProtection);
        _tenantId = $"t-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedTenantAsync(_tenantId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- store

    [Fact]
    public async Task Save_ShouldPersistEncryptedSecret_WhenMfaSecretProvided()
    {
        const string userId = "u-secret";
        const string rawSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

        await _sut.SaveAsync(NewUser(userId, rawSecret, recoveryCodes: null), default);

        var stored = await _fixture.ReadRawMfaSecretAsync(_tenantId, userId);
        stored.Should().NotBeNull();
        stored.Should().NotBe(
            rawSecret,
            because: "A7: users.mfa_secret on disk must NEVER equal the plaintext TOTP shared secret");
        stored!.Length.Should().BeGreaterThan(
            rawSecret.Length,
            because: "DataProtection ciphertext carries a key header and is base64url-encoded — substantially longer than a 32-char Base32 secret");
    }

    [Fact]
    public async Task Save_ShouldPersistEncryptedRecoveryCodes_WhenCodesProvided()
    {
        const string userId = "u-codes";
        string[] supplied = ["digest-alpha", "digest-bravo", "digest-charlie"];

        await _sut.SaveAsync(NewUser(userId, mfaSecret: null, supplied), default);

        var stored = await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId);
        stored.Should().NotBeNull();
        stored!.Should().HaveCount(
            supplied.Length,
            because: "the wrap is per element (design D3) — the column keeps its arity");
        for (var i = 0; i < supplied.Length; i++)
        {
            stored[i].Should().NotBe(
                supplied[i],
                because: "A7: no element on disk may equal the recovery-code digest it wraps");
        }

        stored.Should().NotIntersectWith(
            supplied,
            because: "A7: no stored element may equal ANY supplied digest, not merely its own positional counterpart");
    }

    [Fact]
    public async Task Save_ShouldPersistNull_WhenMfaMaterialIsNull()
    {
        const string userId = "u-null";

        await _sut.SaveAsync(NewUser(userId, mfaSecret: null, recoveryCodes: null), default);

        var storedSecret = await _fixture.ReadRawMfaSecretAsync(_tenantId, userId);
        storedSecret.Should().BeNull(
            because: "null in ⇒ SQL NULL out — the wrap must not turn 'no secret' into the ciphertext of an empty string");

        var storedCodes = await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId);
        storedCodes.Should().BeNull(
            because: "a null recovery-code collection must keep the column's SQL NULL shape");
    }

    [Fact]
    public async Task Save_ShouldPersistEmptyArray_WhenRecoveryCodesEmpty()
    {
        const string userId = "u-empty";

        await _sut.SaveAsync(NewUser(userId, mfaSecret: null, Array.Empty<string>()), default);

        var stored = await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId);
        stored.Should().NotBeNull(
            because: "an empty collection must persist as an empty array, not as SQL NULL");
        stored!.Should().BeEmpty(
            because: "an empty collection must not become a one-element array of ciphertext");
    }

    [Fact]
    public async Task Get_ShouldReturnOriginalMfaMaterial_WhenStoreUsedInternally()
    {
        const string userId = "u-roundtrip";
        const string rawSecret = "KRSXG5CTMVRXEZLUJBSWY3DPEHPK3PXP";
        string[] supplied = ["digest-one", "digest-two"];

        await _sut.SaveAsync(NewUser(userId, rawSecret, supplied), default);

        var byId = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);
        byId.Should().NotBeNull();
        byId!.MfaSecret.Should().Be(
            rawSecret,
            because: "internal callers (MfaService.VerifyCode recomputes the TOTP code from the secret) MUST receive the unwrapped value transparently");
        byId.MfaRecoveryCodes!.Should().Equal(
            supplied,
            because: "recovery-code redemption compares against the original digests, in order");

        var byEmail = await _sut.GetByEmailAsync(new TenantId(_tenantId), $"{userId}@mfa.test", default);
        byEmail.Should().NotBeNull();
        byEmail!.MfaSecret.Should().Be(rawSecret);
        byEmail.MfaRecoveryCodes!.Should().Equal(supplied);

        var byIds = await _sut.GetByIdsAsync(_tenantId, [userId], default);
        byIds.Should().ContainSingle();
        byIds[0].MfaSecret.Should().Be(rawSecret);
        byIds[0].MfaRecoveryCodes!.Should().Equal(supplied);
    }

    [Fact]
    public async Task Get_ShouldReturnLegacyValueVerbatim_WhenRowNotYetMigrated()
    {
        const string userId = "u-legacy";
        const string legacySecret = "LEGACYBASE32SECRET2345678901234";
        string[] legacyCodes = ["legacy-digest-1", "legacy-digest-2"];

        // Planted directly, bypassing the store — the shape a v2.21 database
        // presents between deploy and migrator completion.
        await _fixture.WriteRawMfaMaterialAsync(_tenantId, userId, legacySecret, legacyCodes);

        var loaded = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);

        loaded.Should().NotBeNull(
            because: "an unwrappable legacy row must project, not throw — TOTP verification keeps working during the rollout window");
        loaded!.MfaSecret.Should().Be(
            legacySecret,
            because: "CryptographicException on the scalar means 'legacy', and the stored value is returned verbatim");
        loaded.MfaRecoveryCodes!.Should().Equal(
            legacyCodes,
            because: "legacy array elements are returned verbatim so recovery-code redemption keeps working");
    }

    [Fact]
    public async Task Get_ShouldProjectPerElement_WhenRecoveryCodeArrayPartiallyEncrypted()
    {
        const string userId = "u-mixed";
        const string wrappedSourceA = "wrapped-digest-a";
        const string wrappedSourceB = "wrapped-digest-b";
        const string legacyA = "legacy-digest-a";
        const string legacyB = "legacy-digest-b";

        var protector = _fixture.DataProtection.CreateProtector(
            PostgresUserStore.MfaRecoveryCodesProtectorPurpose);

        // The shape a crash mid-migration leaves behind: some elements wrapped,
        // some still legacy, in one array.
        string[] planted =
        [
            protector.Protect(wrappedSourceA),
            legacyA,
            protector.Protect(wrappedSourceB),
            legacyB,
        ];
        await _fixture.WriteRawMfaMaterialAsync(_tenantId, userId, mfaSecret: null, planted);

        var loaded = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);

        string[] expected = [wrappedSourceA, legacyA, wrappedSourceB, legacyB];
        loaded.Should().NotBeNull();
        loaded!.MfaRecoveryCodes!.Should().Equal(
            expected,
            because: "design D8: the verbatim fallback is evaluated per element, and the stored order and length are preserved");
    }

    [Fact]
    public async Task Get_ShouldPreserveBothHashFormats_WhenCodesMixBcryptAndSha256Hex()
    {
        const string userId = "u-formats";
        // MfaService.HashRecoveryCodes emits BCrypt cost-10 digests;
        // RecoveryCodeService.Hash emits salted SHA-256 hex. Both coexist in
        // the column today and neither may be inspected or normalised.
        const string bcryptDigest = "$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy";
        const string sha256HexDigest = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";
        string[] supplied = [bcryptDigest, sha256HexDigest];

        await _sut.SaveAsync(NewUser(userId, mfaSecret: null, supplied), default);

        var loaded = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);

        loaded.Should().NotBeNull();
        loaded!.MfaRecoveryCodes!.Should().Equal(
            supplied,
            because: "the wrap is format-agnostic — every element is an opaque string, re-hashed or normalised by nothing");
    }

    // ------------------------------------------------------------- migrator

    [Fact]
    public async Task Migration_ShouldEncryptExistingRows_AndBeIdempotent()
    {
        const string userId = "u-migrated";
        const string legacySecret = "LEGACYBASE32FROMV221DATABASE";
        string[] legacyCodes = ["legacy-hash-1", "legacy-hash-2", "legacy-hash-3"];

        await _fixture.WriteRawMfaMaterialAsync(_tenantId, userId, legacySecret, legacyCodes);
        (await _fixture.ReadRawMfaSecretAsync(_tenantId, userId)).Should().Be(
            legacySecret,
            because: "sanity: the row was planted unwrapped, bypassing the store");

        var migrator = NewMigrator();
        await migrator.StartAsync(default);

        var secretAfterFirst = await _fixture.ReadRawMfaSecretAsync(_tenantId, userId);
        var codesAfterFirst = await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId);
        secretAfterFirst.Should().NotBe(
            legacySecret,
            because: "the migrator must wrap the legacy secret into DataProtection ciphertext");
        codesAfterFirst.Should().NotBeNull();
        codesAfterFirst!.Should().HaveCount(legacyCodes.Length);
        codesAfterFirst.Should().NotIntersectWith(
            legacyCodes,
            because: "A7: after migration no element on disk may equal any legacy digest");

        // The store must still round-trip the original values after the rewrite.
        var loaded = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);
        loaded.Should().NotBeNull();
        loaded!.MfaSecret.Should().Be(legacySecret);
        loaded.MfaRecoveryCodes!.Should().Equal(legacyCodes);

        // Second pass — must issue zero writes.
        await migrator.StartAsync(default);

        (await _fixture.ReadRawMfaSecretAsync(_tenantId, userId)).Should().Be(
            secretAfterFirst,
            because: "DataProtection randomises ciphertext per call, so a byte-for-byte identical column proves the idempotent re-run issued no UPDATE");
        (await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId))!.Should().Equal(
            codesAfterFirst,
            because: "an already-wrapped array must be left untouched — a no-op write would churn ciphertext and WAL for nothing");
    }

    [Fact]
    public async Task Migration_ShouldConvergeMixedArray_WhenSomeElementsAlreadyEncrypted()
    {
        const string userId = "u-converge";
        const string legacyFirst = "legacy-digest-first";
        const string legacyLast = "legacy-digest-last";
        const string alreadyWrappedSource = "already-wrapped-digest";

        var protector = _fixture.DataProtection.CreateProtector(
            PostgresUserStore.MfaRecoveryCodesProtectorPurpose);
        var alreadyWrappedCipher = protector.Protect(alreadyWrappedSource);

        string[] planted = [legacyFirst, alreadyWrappedCipher, legacyLast];
        await _fixture.WriteRawMfaMaterialAsync(_tenantId, userId, mfaSecret: null, planted);

        await NewMigrator().StartAsync(default);

        var stored = await _fixture.ReadRawRecoveryCodesAsync(_tenantId, userId);
        stored.Should().NotBeNull();
        stored!.Should().HaveCount(planted.Length, because: "the array's length is preserved");
        stored[0].Should().NotBe(
            legacyFirst,
            because: "A7: the legacy element must be rewritten wrapped");
        stored[2].Should().NotBe(
            legacyLast,
            because: "A7: the legacy element must be rewritten wrapped");
        stored[1].Should().Be(
            alreadyWrappedCipher,
            because: "an already-wrapped element is carried through byte-for-byte — trial unwrap succeeded, so it is never re-wrapped");

        var loaded = await _sut.GetByIdAsync(new TenantId(_tenantId), EntityId.From(userId), default);
        string[] expected = [legacyFirst, alreadyWrappedSource, legacyLast];
        loaded.Should().NotBeNull();
        loaded!.MfaRecoveryCodes!.Should().Equal(
            expected,
            because: "after convergence every element projects to its original value, in the stored order");
    }

    [Fact]
    public async Task Migration_ShouldVisitEveryRow_WhenPopulationExceedsBatchSize()
    {
        // The migrator's BatchSize is 500; overshoot it so the keyset cursor has
        // to advance at least twice. A broken cursor either loops forever or
        // skips the rows past the first batch.
        const int rowCount = 620;

        var seed = new List<RawMfaSeedRow>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            seed.Add(new RawMfaSeedRow
            {
                UserId = $"bulk-{i:D4}",
                MfaSecret = LegacySecretFor(i),
            });
        }
        await _fixture.WriteRawMfaMaterialBatchAsync(_tenantId, seed);

        await NewMigrator().StartAsync(default);

        var stored = await _fixture.ReadAllRawMfaSecretsAsync(_tenantId);
        stored.Should().HaveCount(rowCount, because: "sanity: the whole population was planted");
        for (var i = 0; i < rowCount; i++)
        {
            stored[$"bulk-{i:D4}"].Should().NotBe(
                LegacySecretFor(i),
                because: "A7: every row past the first batch must be wrapped too — a skipped row would still hold its plaintext");
        }

        var loaded = await _sut.GetByIdsAsync(_tenantId, seed.Select(r => r.UserId).ToList(), default);
        loaded.Should().HaveCount(rowCount);
        var byUserId = loaded.ToDictionary(u => u.UserId.Value, u => u.MfaSecret, StringComparer.Ordinal);
        for (var i = 0; i < rowCount; i++)
        {
            byUserId[$"bulk-{i:D4}"].Should().Be(
                LegacySecretFor(i),
                because: "one unwrap recovers the original, so each row was wrapped exactly once — a twice-visited row would project as ciphertext");
        }
    }

    [Fact]
    public async Task Migration_ShouldNotThrow_WhenUsersTableMissing()
    {
        await _fixture.DropUsersTableAsync();
        try
        {
            var migrator = NewMigrator();

            var act = async () => await migrator.StartAsync(default);

            await act.Should().NotThrowAsync(
                because: "SQLSTATE 42P01 (undefined_table) is a silent no-op — the migrator must never block host startup on a fresh install whose schema runner has not landed yet");
        }
        finally
        {
            await _fixture.EnsureSchemaAsync();
        }
    }

    [Fact]
    public void Protector_ShouldThrowCryptographicException_WhenPurposeMismatched()
    {
        var secretProtector = _fixture.DataProtection.CreateProtector(
            PostgresUserStore.MfaSecretProtectorPurpose);
        var recoveryCodesProtector = _fixture.DataProtection.CreateProtector(
            PostgresUserStore.MfaRecoveryCodesProtectorPurpose);
        var ciphertext = secretProtector.Protect("JBSWY3DPEHPK3PXP");

        var act = () => recoveryCodesProtector.Unprotect(ciphertext);

        act.Should().Throw<CryptographicException>(
            because: "design D2: each column binds its own concern-specific purpose, so a protector for one concern can never decrypt the other's ciphertext");
    }

    // -------------------------------------------------------------- helpers

    private static string LegacySecretFor(int index) => $"LEGACY-BULK-SECRET-{index:D4}";

    private UserMfaEncryptionMigrator NewMigrator() => new(
        _fixture.DataSource,
        _fixture.DataProtection,
        NullLogger<UserMfaEncryptionMigrator>.Instance);

    private User NewUser(string userId, string? mfaSecret, IReadOnlyList<string>? recoveryCodes) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(_tenantId),
        Email = $"{userId}@mfa.test",
        DisplayName = "MFA Encryption Subject",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        MfaEnabled = true,
        MfaSecret = mfaSecret,
        MfaRecoveryCodes = recoveryCodes,
    };
}
