using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Stores;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// ADMIN-001 (PREPUB-2026-05-09) regression suite. Asserts that
/// <see cref="PostgresTenantAuthConfigStore"/> never persists the OIDC
/// client secret in plaintext, that <see cref="PostgresTenantAuthConfigStore.GetAsync"/>
/// transparently unwraps stored ciphertext for internal callers (notably
/// the OIDC token-exchange flow), and that the
/// <see cref="OidcClientSecretEncryptionMigrator"/> idempotently rewrites
/// any legacy plaintext rows it finds at host startup.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTenantAuthConfigStoreEncryptionTests
    : IClassFixture<TenantAuthConfigEncryptionFixture>, IAsyncLifetime
{
    private readonly TenantAuthConfigEncryptionFixture _fixture;
    private readonly PostgresTenantAuthConfigStore _sut;
    private readonly string _tenantId;

    public PostgresTenantAuthConfigStoreEncryptionTests(TenantAuthConfigEncryptionFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresTenantAuthConfigStore(_fixture.DataSource, _fixture.DataProtection);
        _tenantId = $"t-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedTenantAsync(_tenantId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Save_ShouldPersistEncrypted_WhenOidcClientSecretProvided()
    {
        const string raw = "raw-secret-do-not-store-as-bytes";
        await _sut.SaveAsync(new TenantAuthConfig
        {
            TenantId = _tenantId,
            OidcEnabled = true,
            OidcAuthority = "https://idp.test",
            OidcClientId = "client",
            OidcClientSecret = raw,
        }, default);

        var rawColumn = await _fixture.ReadRawSecretAsync(_tenantId);
        rawColumn.Should().NotBeNull();
        rawColumn.Should().NotBe(
            raw,
            because: "ADMIN-001: the column on disk must NEVER equal the plaintext value");
        rawColumn!.Length.Should().BeGreaterThan(
            raw.Length,
            because: "DataProtection ciphertext is base64-encoded and includes a header — substantially longer than a 32-char input");
    }

    [Fact]
    public async Task Get_ShouldReturnOriginalSecret_WhenStoreUsedInternally()
    {
        const string raw = "round-trip-target-1234567890";
        await _sut.SaveAsync(new TenantAuthConfig
        {
            TenantId = _tenantId,
            OidcEnabled = true,
            OidcAuthority = "https://idp.test",
            OidcClientId = "client",
            OidcClientSecret = raw,
        }, default);

        var loaded = await _sut.GetAsync(_tenantId, default);
        loaded.Should().NotBeNull();
        loaded!.OidcClientSecret.Should().Be(
            raw,
            because: "internal callers (OidcEndpoints token-exchange) MUST receive the unwrapped value transparently");
    }

    [Fact]
    public async Task Migration_ShouldEncryptExistingRows_AndBeIdempotent()
    {
        const string legacyPlaintext = "legacy-from-v1.12-database";
        // Insert a legacy plaintext row directly, bypassing the store.
        await _fixture.InsertRawAsync(_tenantId, legacyPlaintext);

        var rawBefore = await _fixture.ReadRawSecretAsync(_tenantId);
        rawBefore.Should().Be(legacyPlaintext, because: "sanity: the row was inserted plaintext");

        // First migrator pass — re-encrypts the legacy row.
        var migrator = new OidcClientSecretEncryptionMigrator(
            _fixture.DataSource, _fixture.DataProtection, NullLogger<OidcClientSecretEncryptionMigrator>.Instance);
        await migrator.StartAsync(default);

        var rawAfterFirst = await _fixture.ReadRawSecretAsync(_tenantId);
        rawAfterFirst.Should().NotBe(
            legacyPlaintext,
            because: "the migrator must wrap legacy plaintext into DataProtection ciphertext");
        rawAfterFirst.Should().NotBeNullOrEmpty();

        // The store must still round-trip the original value.
        var loaded = await _sut.GetAsync(_tenantId, default);
        loaded!.OidcClientSecret.Should().Be(legacyPlaintext);

        // Second migrator pass — must be a no-op.
        await migrator.StartAsync(default);
        var rawAfterSecond = await _fixture.ReadRawSecretAsync(_tenantId);
        rawAfterSecond.Should().Be(
            rawAfterFirst,
            because: "the migrator must be idempotent — already-encrypted rows are left untouched on re-runs");
    }

    [Fact]
    public async Task Save_ShouldPersistNull_WhenOidcClientSecretIsNullOrEmpty()
    {
        await _sut.SaveAsync(new TenantAuthConfig
        {
            TenantId = _tenantId,
            OidcEnabled = false,
            OidcClientSecret = null,
        }, default);

        var rawColumn = await _fixture.ReadRawSecretAsync(_tenantId);
        rawColumn.Should().BeNull();

        var loaded = await _sut.GetAsync(_tenantId, default);
        loaded!.OidcClientSecret.Should().BeNull();
    }
}
