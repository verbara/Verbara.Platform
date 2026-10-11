using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// whatsapp-works-for-real 6.4 (design D15, spec <c>channel-credential-config</c>): the Postgres channel
/// configuration store wraps every secret credential value with DataProtection (purpose
/// <c>Verbara.Platform.TenantChannelConfig.Credentials.v1</c>) before it reaches the <c>credentials</c>
/// column and unwraps on read, falling back verbatim for a legacy value; the one-shot
/// <see cref="TenantChannelCredentialsEncryptionMigrator"/> wraps legacy plaintext once and never blocks.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTenantChannelConfigStoreEncryptionTests
    : IClassFixture<TenantChannelConfigEncryptionFixture>, IAsyncLifetime
{
    private const int WhatsApp = (int)ChannelType.WhatsApp;

    private readonly TenantChannelConfigEncryptionFixture _fixture;
    private readonly PostgresTenantChannelConfigStore _sut;
    private readonly string _tenantId = $"t-{Guid.NewGuid():N}";

    public PostgresTenantChannelConfigStoreEncryptionTests(TenantChannelConfigEncryptionFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresTenantChannelConfigStore(_fixture.DataSource, _fixture.DataProtection);
    }

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveAsync_ShouldStoreProtectedValue_WhenKeyIsSecret()
    {
        var secrets = Secrets();

        await _sut.SaveAsync(Config(ChannelType.WhatsApp, secrets), CancellationToken.None);

        var raw = await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp);
        raw.Should().NotBeNull();
        raw.Should().NotContain(secrets["AppSecret"], "a secret value never reaches the column in clear");
        raw.Should().NotContain(secrets["AccessToken"]);
        raw.Should().NotContain(secrets["WebhookVerifyToken"]);
        raw.Should().Contain(secrets["PhoneNumberId"], "a non-secret key is stored as is");
        var loaded = await _sut.GetAsync(new TenantId(_tenantId), ChannelType.WhatsApp, CancellationToken.None);
        loaded!.Credentials.Should().Equal(secrets, "the store unwraps on read");
    }

    [Fact]
    public async Task SaveAsync_ShouldProtectEveryKey_WhenChannelHasNoSchema()
    {
        var credentials = new Dictionary<string, string> { ["BotToken"] = $"bot-{Guid.NewGuid():N}" };

        await _sut.SaveAsync(Config(ChannelType.Telegram, credentials), CancellationToken.None);

        var raw = await _fixture.ReadRawCredentialsAsync(_tenantId, (int)ChannelType.Telegram);
        raw.Should().NotContain(credentials["BotToken"]);
        (await _sut.GetAsync(new TenantId(_tenantId), ChannelType.Telegram, CancellationToken.None))!
            .Credentials.Should().Equal(credentials);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnLegacyValueVerbatim_WhenNotYetWrapped()
    {
        var secrets = Secrets();
        await _fixture.WriteRawAsync(_tenantId, WhatsApp, Json(secrets));

        var loaded = await _sut.GetAsync(new TenantId(_tenantId), ChannelType.WhatsApp, CancellationToken.None);

        loaded!.Credentials.Should().Equal(secrets, "a value the migrator has not reached is returned as stored");
    }

    [Fact]
    public async Task Migrator_ShouldWrapLegacyPlaintextOnce_WhenRunTwice()
    {
        var secrets = Secrets();
        await _fixture.WriteRawAsync(_tenantId, WhatsApp, Json(secrets));
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(1);
        var afterFirst = await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp);
        afterFirst.Should().NotContain(secrets["AppSecret"]).And.NotContain(secrets["AccessToken"])
            .And.NotContain(secrets["WebhookVerifyToken"]);
        afterFirst.Should().Contain(secrets["PhoneNumberId"]);
        (await _sut.GetAsync(new TenantId(_tenantId), ChannelType.WhatsApp, CancellationToken.None))!
            .Credentials.Should().Equal(secrets);

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(0, "the second run finds every secret wrapped and writes nothing");
        migrator.LastRun.ScannedRows.Should().Be(1);
        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().Be(
            afterFirst, "a wrapped row is never rewritten (a re-wrap would change the ciphertext)");
    }

    [Fact]
    public async Task Migrator_ShouldWrapLegacyApiToken_WhenKeyIsOutsideWhatsAppSchema()
    {
        // Before Web #393 every console save of WhatsApp wrote the Meta token as ApiToken.
        var legacyToken = $"EAAlegacy{Guid.NewGuid():N}";
        var legacy = new Dictionary<string, string> { ["ApiToken"] = legacyToken, ["PhoneNumber"] = "+15550001111" };
        await _fixture.WriteRawAsync(_tenantId, WhatsApp, Json(legacy));
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(1);
        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().NotContain(
            legacyToken, "a key outside the schema may hold a real secret and is wrapped like one");
        (await _sut.GetAsync(new TenantId(_tenantId), ChannelType.WhatsApp, CancellationToken.None))!
            .Credentials.Should().Equal(legacy);
    }

    [Fact]
    public async Task SaveAsync_ShouldProtectLegacyKey_WhenKeyIsOutsideWhatsAppSchema()
    {
        var legacyToken = $"EAAlegacy{Guid.NewGuid():N}";

        await _sut.SaveAsync(
            Config(ChannelType.WhatsApp, new Dictionary<string, string> { ["ApiToken"] = legacyToken }), CancellationToken.None);

        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().NotContain(legacyToken);
    }

    [Fact]
    public async Task Migrator_ShouldWrapOnlyPlaintextValues_WhenRowIsMixed()
    {
        var secrets = Secrets();
        var protector = _fixture.DataProtection.CreateProtector(PostgresTenantChannelConfigStore.CredentialsProtectorPurpose);
        var wrappedAppSecret = protector.Protect(secrets["AppSecret"]);
        var mixed = new Dictionary<string, string>(secrets) { ["AppSecret"] = wrappedAppSecret };
        await _fixture.WriteRawAsync(_tenantId, WhatsApp, Json(mixed));
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(1);
        var raw = await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp);
        var stored = JsonSerializer.Deserialize(raw!, PostgresJson.Ctx.IReadOnlyDictionaryStringString)!;
        stored["AppSecret"].Should().Be(wrappedAppSecret, "an already wrapped value is carried through byte for byte");
        stored["AccessToken"].Should().NotBe(secrets["AccessToken"]);
        protector.Unprotect(stored["AccessToken"]).Should().Be(
            secrets["AccessToken"], "a plaintext value is wrapped exactly once");
        (await _sut.GetAsync(new TenantId(_tenantId), ChannelType.WhatsApp, CancellationToken.None))!
            .Credentials.Should().Equal(secrets);
    }

    [Fact]
    public async Task Migrator_ShouldRewriteNothing_WhenKeyRingRotatedAfterWrite()
    {
        await _sut.SaveAsync(Config(ChannelType.WhatsApp, Secrets()), CancellationToken.None);
        var before = await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp);
        _fixture.RotateKey();
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(0, "a value under an older key still unprotects and is not re-wrapped");
        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().Be(before);
    }

    [Fact]
    public async Task Migrator_ShouldLeaveStoreWrittenRowUntouched_WhenAlreadyProtected()
    {
        await _sut.SaveAsync(Config(ChannelType.WhatsApp, Secrets()), CancellationToken.None);
        var before = await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp);
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.RewrittenRows.Should().Be(0);
        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().Be(before);
    }

    [Fact]
    public async Task Migrator_ShouldNotThrow_WhenTableMissing()
    {
        await _fixture.DropTableAsync();
        try
        {
            var act = async () => await NewMigrator().StartAsync(CancellationToken.None);

            await act.Should().NotThrowAsync("a missing table must never block host startup");
        }
        finally
        {
            await _fixture.EnsureSchemaAsync();
        }
    }

    [Fact]
    public async Task Migrator_ShouldSkipRowAndContinue_WhenCredentialsAreNotAnObject()
    {
        var other = $"t-{Guid.NewGuid():N}";
        var secrets = Secrets();
        await _fixture.WriteRawAsync(other, WhatsApp, "[\"not-an-object\"]");
        await _fixture.WriteRawAsync(_tenantId, WhatsApp, Json(secrets));
        var migrator = NewMigrator();

        await migrator.StartAsync(CancellationToken.None);

        migrator.LastRun.FailedRows.Should().Be(1);
        migrator.LastRun.RewrittenRows.Should().Be(1, "one bad row does not abandon the rest");
        (await _fixture.ReadRawCredentialsAsync(_tenantId, WhatsApp)).Should().NotContain(secrets["AppSecret"]);
    }

    private TenantChannelCredentialsEncryptionMigrator NewMigrator() => new(
        _fixture.DataSource, _fixture.DataProtection, NullLogger<TenantChannelCredentialsEncryptionMigrator>.Instance);

    private TenantChannelConfig Config(ChannelType channel, Dictionary<string, string> credentials) => new()
    {
        TenantId = new TenantId(_tenantId),
        Channel = channel,
        IsActive = true,
        Credentials = credentials,
    };

    private static Dictionary<string, string> Secrets()
    {
        var n = Guid.NewGuid().ToString("N");
        return new Dictionary<string, string>
        {
            ["AccessToken"] = $"EAAtoken{n}",
            ["PhoneNumberId"] = $"pn{n}",
            ["AppSecret"] = $"app-secret-{n}",
            ["WebhookVerifyToken"] = $"verify-{n}",
        };
    }

    private static string Json(Dictionary<string, string> credentials) =>
        JsonSerializer.Serialize(credentials, PostgresJson.Ctx.IReadOnlyDictionaryStringString);
}
