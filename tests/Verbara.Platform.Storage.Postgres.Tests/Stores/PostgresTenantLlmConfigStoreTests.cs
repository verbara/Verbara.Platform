using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// C3 (P2c.1) — Testcontainers-backed round-trip suite for <see cref="PostgresTenantLlmConfigStore"/>.
/// Reuses <see cref="TenantLlmConfigSeedFixture"/> (it provisions the <c>tenant_llm_config</c> table
/// + an ephemeral DataProtection key ring). Asserts: upsert→get returns the DECRYPTED key while the
/// raw column ciphertext ≠ plaintext, the JSONB <c>provider_settings</c> persists for Azure/Anthropic,
/// key rotation, and delete.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresTenantLlmConfigStoreTests
    : IClassFixture<TenantLlmConfigSeedFixture>, IAsyncLifetime
{
    private readonly TenantLlmConfigSeedFixture _fixture;
    private readonly string _tenantId;
    private readonly PostgresTenantLlmConfigStore _store;

    public PostgresTenantLlmConfigStoreTests(TenantLlmConfigSeedFixture fixture)
    {
        _fixture = fixture;
        _tenantId = $"t-{Guid.NewGuid():N}";
        _store = new PostgresTenantLlmConfigStore(
            _fixture.DataSource,
            _fixture.DataProtection,
            NullLogger<PostgresTenantLlmConfigStore>.Instance);
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TenantLlmConfig Config(
        ProviderType providerType = ProviderType.OpenAiCompatible,
        string model = "gpt-4o-mini",
        string? apiKey = "sk-roundtrip-1234",
        ProviderSettings? settings = null,
        DateTimeOffset? updatedAt = null)
    {
        var now = updatedAt ?? DateTimeOffset.UtcNow;
        return new TenantLlmConfig
        {
            TenantId = EntityId.From(_tenantId),
            ProviderType = providerType,
            Model = model,
            ApiKey = apiKey,
            ApiKeyLast4 = apiKey is { Length: >= 4 } ? apiKey[^4..] : null,
            Settings = settings ?? new ProviderSettings { BaseUrl = "https://api.openai.com/v1" },
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task UpsertThenGet_ShouldReturnDecryptedKey_AndStoreCiphertext()
    {
        await _store.UpsertAsync(Config(apiKey: "sk-secret-key-7777"), CancellationToken.None);

        var loaded = await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.ApiKey.Should().Be("sk-secret-key-7777", because: "the store unwraps the encrypted key on read");
        loaded.ApiKeyLast4.Should().Be("7777");
        loaded.Model.Should().Be("gpt-4o-mini");
        loaded.Settings.BaseUrl.Should().Be("https://api.openai.com/v1");

        // The raw column must hold ciphertext, never the plaintext key.
        var raw = await _fixture.ReadRawKeyAsync(_tenantId);
        raw.Should().NotBeNull();
        raw.Should().NotBe("sk-secret-key-7777");
    }

    [Fact]
    public async Task UpsertThenGet_ShouldPersistAzureSettings_InJsonbColumn()
    {
        var settings = new ProviderSettings
        {
            BaseUrl = "https://r.openai.azure.com",
            AzureDeployment = "prod-gpt4o",
            AzureApiVersion = "2024-06-01",
        };
        await _store.UpsertAsync(
            Config(ProviderType.AzureOpenAi, model: "gpt-4o", apiKey: "sk-azure-9999", settings: settings),
            CancellationToken.None);

        var loaded = await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.ProviderType.Should().Be(ProviderType.AzureOpenAi);
        loaded.Settings.AzureDeployment.Should().Be("prod-gpt4o");
        loaded.Settings.AzureApiVersion.Should().Be("2024-06-01");
        loaded.Settings.BaseUrl.Should().Be("https://r.openai.azure.com");
    }

    [Fact]
    public async Task UpsertThenGet_ShouldPersistAnthropicSettings_InJsonbColumn()
    {
        var settings = new ProviderSettings { AnthropicVersion = "2024-10-22" };
        await _store.UpsertAsync(
            Config(ProviderType.Anthropic, model: "claude-3-5-haiku-latest", apiKey: "sk-ant-8888", settings: settings),
            CancellationToken.None);

        var loaded = await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.ProviderType.Should().Be(ProviderType.Anthropic);
        loaded.Settings.AnthropicVersion.Should().Be("2024-10-22");
    }

    [Fact]
    public async Task Upsert_ShouldRotateKey_OnSecondUpsert()
    {
        await _store.UpsertAsync(Config(apiKey: "sk-original-0000"), CancellationToken.None);
        var rawBefore = await _fixture.ReadRawKeyAsync(_tenantId);

        await _store.UpsertAsync(Config(apiKey: "sk-rotated-1111"), CancellationToken.None);

        var loaded = await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None);
        loaded!.ApiKey.Should().Be("sk-rotated-1111");
        loaded.ApiKeyLast4.Should().Be("1111");

        var rawAfter = await _fixture.ReadRawKeyAsync(_tenantId);
        rawAfter.Should().NotBe(rawBefore, because: "a rotated key produces fresh ciphertext");

        // Still exactly one row (upsert, not insert).
        (await _fixture.CountConfigRowsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Delete_ShouldRemoveRow()
    {
        await _store.UpsertAsync(Config(), CancellationToken.None);
        (await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None)).Should().NotBeNull();

        await _store.DeleteAsync(EntityId.From(_tenantId), CancellationToken.None);

        (await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None)).Should().BeNull();
        (await _fixture.CountConfigRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Get_ShouldReturnNull_WhenNoRow()
    {
        var loaded = await _store.GetAsync(EntityId.From(_tenantId), CancellationToken.None);
        loaded.Should().BeNull();
    }
}
