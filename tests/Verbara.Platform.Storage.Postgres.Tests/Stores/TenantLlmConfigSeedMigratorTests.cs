using Verbara.Platform.Llm;
using Verbara.Platform.Storage.Postgres.Stores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// C3 (P2c.1, spec §6) — <see cref="TenantLlmConfigSeedMigrator"/> regression suite. Asserts the
/// seed materialises the appsettings global LLM key into the single operational tenant's row ONLY
/// when (configured + exactly one operational tenant + no existing row), is otherwise a no-op, and
/// is idempotent across re-runs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantLlmConfigSeedMigratorTests
    : IClassFixture<TenantLlmConfigSeedFixture>, IAsyncLifetime
{
    private readonly TenantLlmConfigSeedFixture _fixture;
    private readonly string _tenantId;

    public TenantLlmConfigSeedMigratorTests(TenantLlmConfigSeedFixture fixture)
    {
        _fixture = fixture;
        _tenantId = $"t-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync() => await _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private TenantLlmConfigSeedMigrator BuildMigrator(LlmProviderOptions options) =>
        new(
            _fixture.DataSource,
            _fixture.DataProtection,
            Options.Create(options),
            NullLogger<TenantLlmConfigSeedMigrator>.Instance);

    private static LlmProviderOptions Configured() => new()
    {
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = "sk-global-key-5678",
        Model = "gpt-4o-mini",
    };

    [Fact]
    public async Task Seed_ShouldMaterializeConfig_WhenSingleOperationalTenantAndNoRow()
    {
        await _fixture.SeedTenantAsync(_tenantId, TenantLlmConfigSeedFixture.CustomerTenantType);

        await BuildMigrator(Configured()).StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(1);

        var store = new PostgresTenantLlmConfigStore(_fixture.DataSource, _fixture.DataProtection);
        var config = await store.GetAsync(Verbara.Platform.Core.EntityId.From(_tenantId), default);
        config.Should().NotBeNull();
        config!.ProviderType.Should().Be(ProviderType.OpenAiCompatible);
        config.Model.Should().Be("gpt-4o-mini");
        config.Enabled.Should().BeTrue();
        config.Settings.BaseUrl.Should().Be("https://api.openai.com/v1");
        config.ApiKey.Should().Be("sk-global-key-5678", because: "the store unwraps the encrypted key");
        config.ApiKeyLast4.Should().Be("5678");

        // The key on disk is encrypted, never plaintext.
        var raw = await _fixture.ReadRawKeyAsync(_tenantId);
        raw.Should().NotBeNull();
        raw.Should().NotBe("sk-global-key-5678");
    }

    [Fact]
    public async Task Seed_ShouldNoOp_WhenKeyUnset()
    {
        await _fixture.SeedTenantAsync(_tenantId, TenantLlmConfigSeedFixture.CustomerTenantType);

        // No ApiKey ⇒ LlmProviderOptions.IsConfigured == false ⇒ no-op.
        await BuildMigrator(new LlmProviderOptions { BaseUrl = "https://x/v1", Model = "m" }).StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ShouldNoOp_WhenRowExists()
    {
        await _fixture.SeedTenantAsync(_tenantId, TenantLlmConfigSeedFixture.CustomerTenantType);
        await _fixture.InsertConfigRawAsync(_tenantId, model: "preexisting-model");

        await BuildMigrator(Configured()).StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(1);
        var store = new PostgresTenantLlmConfigStore(_fixture.DataSource, _fixture.DataProtection);
        var config = await store.GetAsync(Verbara.Platform.Core.EntityId.From(_tenantId), default);
        config!.Model.Should().Be("preexisting-model", because: "the seed must never clobber an existing row");
    }

    [Fact]
    public async Task Seed_ShouldNoOp_WhenMultipleOperationalTenants()
    {
        await _fixture.SeedTenantAsync($"{_tenantId}-a", TenantLlmConfigSeedFixture.CustomerTenantType);
        await _fixture.SeedTenantAsync($"{_tenantId}-b", TenantLlmConfigSeedFixture.CustomerTenantType);

        await BuildMigrator(Configured()).StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ShouldNoOp_WhenOnlyNonOperationalTenant()
    {
        // A single Platform tenant is NOT operational — nothing to seed.
        await _fixture.SeedTenantAsync(_tenantId, TenantLlmConfigSeedFixture.PlatformTenantType);

        await BuildMigrator(Configured()).StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ShouldBeIdempotent_OnSecondRun()
    {
        await _fixture.SeedTenantAsync(_tenantId, TenantLlmConfigSeedFixture.CustomerTenantType);

        var migrator = BuildMigrator(Configured());
        await migrator.StartAsync(default);
        var rawAfterFirst = await _fixture.ReadRawKeyAsync(_tenantId);

        await migrator.StartAsync(default);

        (await _fixture.CountConfigRowsAsync()).Should().Be(1);
        var rawAfterSecond = await _fixture.ReadRawKeyAsync(_tenantId);
        rawAfterSecond.Should().Be(rawAfterFirst, because: "a re-run finds the row already present and writes nothing");
    }
}
