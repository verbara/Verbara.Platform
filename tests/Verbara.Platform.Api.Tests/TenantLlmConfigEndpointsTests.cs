using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// C2 (P2c.1) — <c>/admin/ai/llm-config</c> admin surface: masked GET (key → keySet + keyLast4),
/// upsert with stored-key preservation, the <c>typification:ai:configure</c> 403 gate, the
/// <c>POST /test</c> probe (saved / draft / failure), and DELETE.
/// </summary>
public sealed class TenantLlmConfigEndpointsTests
{
    private const string TenantId = LlmConfigApiFactory.TestTenantId;

    // ─── GET ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ShouldReturnNotConfigured_WhenNoRow()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["configured"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Get_ShouldMaskKey_WhenConfigured()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, apiKey: "sk-secret-7777", model: "gpt-4o-mini");

        var response = await client.GetAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["keySet"]!.GetValue<bool>().Should().BeTrue();
        json["keyLast4"]!.GetValue<string>().Should().Be("7777");
        json["model"]!.GetValue<string>().Should().Be("gpt-4o-mini");
        json["enabled"]!.GetValue<bool>().Should().BeTrue();
        // The raw key must NEVER appear anywhere in the payload.
        (await response.Content.ReadAsStringAsync()).Should().NotContain("sk-secret-7777");
    }

    // ─── PUT ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_ShouldEncryptAndPersist_WhenKeyProvided()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = "sk-brand-new-1234",
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["keySet"]!.GetValue<bool>().Should().BeTrue();
        json["keyLast4"]!.GetValue<string>().Should().Be("1234");

        // The persisted config carries the plaintext key (InMemory store) + last4.
        var stored = await LoadConfigAsync(factory);
        stored.Should().NotBeNull();
        stored!.ApiKey.Should().Be("sk-brand-new-1234");
        stored.ApiKeyLast4.Should().Be("1234");
        stored.Model.Should().Be("gpt-4o-mini");
        stored.Settings.BaseUrl.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public async Task Put_ShouldPreserveStoredKey_WhenKeyOmitted()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, apiKey: "sk-original-9999", model: "gpt-4o-mini");

        // Edit settings WITHOUT supplying a key → stored key must be preserved.
        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o",
            apiKey = (string?)null,
            settings = new { baseUrl = "https://new.example/v1" },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await LoadConfigAsync(factory);
        stored.Should().NotBeNull();
        stored!.ApiKey.Should().Be("sk-original-9999", because: "an omitted key preserves the stored key");
        stored.ApiKeyLast4.Should().Be("9999");
        stored.Model.Should().Be("gpt-4o", because: "the non-key fields are still updated");
        stored.Settings.BaseUrl.Should().Be("https://new.example/v1");
    }

    [Fact]
    public async Task Put_ShouldInvalidateResolver_WhenUpserted()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = "sk-x-0001",
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
        };

        await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        factory.Resolver.Received().Invalidate(Arg.Is<EntityId>(e => e.Value == TenantId));
    }

    // ─── DELETE ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ShouldRemoveRow()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, apiKey: "sk-to-delete-0000", model: "gpt-4o-mini");
        (await LoadConfigAsync(factory)).Should().NotBeNull();

        var response = await client.DeleteAsync("/api/admin/ai/llm-config");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoadConfigAsync(factory)).Should().BeNull();
        factory.Resolver.Received().Invalidate(Arg.Is<EntityId>(e => e.Value == TenantId));
    }

    // ─── 403 gate ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoints_ShouldReturn403_WithoutPermission()
    {
        // The default authenticated factory's admin has no typification:ai:configure permission.
        using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var get = await client.GetAsync("/api/admin/ai/llm-config");
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var put = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = "sk-nope",
            settings = new { baseUrl = "https://api.openai.com/v1" },
            enabled = true,
        }));
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var del = await client.DeleteAsync("/api/admin/ai/llm-config");
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var test = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(new { }));
        test.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── POST /test ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test_ShouldReturnReachableTrue_WhenProviderResponds()
    {
        using var factory = new LlmConfigApiFactory();
        factory.ProbeProvider = new StubProvider(succeed: true);
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, apiKey: "sk-ok-1111", model: "gpt-4o-mini");

        var response = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["reachable"]!.GetValue<bool>().Should().BeTrue();
        json["authOk"]!.GetValue<bool>().Should().BeTrue();
        json["modelOk"]!.GetValue<bool>().Should().BeTrue();
        json["error"]?.GetValue<string?>().Should().BeNull();
    }

    [Fact]
    public async Task Test_ShouldReturnError_WhenProviderFails()
    {
        using var factory = new LlmConfigApiFactory();
        factory.ProbeProvider = new StubProvider(
            succeed: false,
            failure: new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized));
        using var client = factory.CreateAuthenticatedClient();

        await SeedConfigAsync(factory, apiKey: "sk-bad-2222", model: "gpt-4o-mini");

        var response = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // 401 → reachable but auth failed; model not ok; an error string is present.
        json!["reachable"]!.GetValue<bool>().Should().BeTrue();
        json["authOk"]!.GetValue<bool>().Should().BeFalse();
        json["modelOk"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Test_ShouldUseDraft_WithoutPersisting()
    {
        using var factory = new LlmConfigApiFactory();
        factory.ProbeProvider = new StubProvider(succeed: true);
        using var client = factory.CreateAuthenticatedClient();

        // No saved config — probe a DRAFT supplied in the body.
        var body = new
        {
            providerType = "OpenAiCompatible",
            model = "gpt-4o-mini",
            apiKey = "sk-draft-3333",
            settings = new { baseUrl = "https://draft.example/v1" },
        };

        var response = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["reachable"]!.GetValue<bool>().Should().BeTrue();
        json["modelOk"]!.GetValue<bool>().Should().BeTrue();

        // The draft was NOT persisted — GET still reports no provider.
        (await LoadConfigAsync(factory)).Should().BeNull();

        // The resolver built a transient provider from the draft config (in-memory only).
        factory.Resolver.Received().BuildTransient(
            Arg.Is<TenantLlmConfig>(c => c.ApiKey == "sk-draft-3333" && c.Model == "gpt-4o-mini"));
    }

    [Fact]
    public async Task Test_ShouldRequireExplicitKey_WhenDraftOverridesDestination()
    {
        using var factory = new LlmConfigApiFactory();
        factory.ProbeProvider = new StubProvider(succeed: true);
        using var client = factory.CreateAuthenticatedClient();

        // Saved config points at the OpenAI endpoint with a stored key.
        await SeedConfigAsync(factory, apiKey: "sk-stored-9999", model: "gpt-4o-mini");

        // A draft that REDIRECTS the base url to an attacker endpoint but omits the key MUST NOT fall
        // back to the stored key — the stored credential could be exfiltrated to the new destination.
        var body = new
        {
            model = "gpt-4o-mini",
            settings = new { baseUrl = "https://attacker.example/v1" },
        };

        var response = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["reachable"]!.GetValue<bool>().Should().BeFalse();
        json["authOk"]!.GetValue<bool>().Should().BeFalse();
        json["modelOk"]!.GetValue<bool>().Should().BeFalse();
        json["error"]!.GetValue<string>().Should().Contain("explicit apiKey");

        // The probe must NEVER have been built — the stored key was never materialised for the draft.
        factory.Resolver.DidNotReceive().BuildTransient(Arg.Any<TenantLlmConfig>());
    }

    [Fact]
    public async Task Test_ShouldUseStoredKey_WhenDraftKeepsSameDestination()
    {
        using var factory = new LlmConfigApiFactory();
        factory.ProbeProvider = new StubProvider(succeed: true);
        using var client = factory.CreateAuthenticatedClient();

        // Saved config + stored key on the OpenAI endpoint.
        await SeedConfigAsync(factory, apiKey: "sk-stored-8888", model: "gpt-4o-mini");

        // A draft that changes ONLY the model (same base url, same provider type) may reuse the
        // stored key — the destination is unchanged so there's no exfiltration risk.
        var body = new
        {
            model = "gpt-4o",
            settings = new { baseUrl = "https://api.openai.com/v1" },
        };

        var response = await client.PostAsync("/api/admin/ai/llm-config/test", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["reachable"]!.GetValue<bool>().Should().BeTrue();

        // The probe reused the stored key for the same-destination draft.
        factory.Resolver.Received().BuildTransient(
            Arg.Is<TenantLlmConfig>(c => c.ApiKey == "sk-stored-8888" && c.Model == "gpt-4o"));
    }

    // ─── Azure required-field validation ──────────────────────────────────────────────

    [Fact]
    public async Task Put_ShouldReturn400_WhenAzureDeploymentMissing()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "AzureOpenAi",
            model = "gpt-4o",
            apiKey = "sk-azure-1234",
            settings = new { baseUrl = "https://r.openai.azure.com", azureApiVersion = "2024-06-01" },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadConfigAsync(factory)).Should().BeNull(because: "a malformed Azure config must not persist");
    }

    [Fact]
    public async Task Put_ShouldReturn400_WhenAzureApiVersionMissing()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "AzureOpenAi",
            model = "gpt-4o",
            apiKey = "sk-azure-1234",
            settings = new { baseUrl = "https://r.openai.azure.com", azureDeployment = "prod-gpt4o" },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadConfigAsync(factory)).Should().BeNull(because: "a malformed Azure config must not persist");
    }

    [Fact]
    public async Task Put_ShouldPersist_WhenAzureDeploymentAndVersionPresent()
    {
        using var factory = new LlmConfigApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new
        {
            providerType = "AzureOpenAi",
            model = "gpt-4o",
            apiKey = "sk-azure-1234",
            settings = new
            {
                baseUrl = "https://r.openai.azure.com",
                azureDeployment = "prod-gpt4o",
                azureApiVersion = "2024-06-01",
            },
            enabled = true,
        };

        var response = await client.PutAsync("/api/admin/ai/llm-config", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await LoadConfigAsync(factory);
        stored.Should().NotBeNull();
        stored!.ProviderType.Should().Be(ProviderType.AzureOpenAi);
        stored.Settings.AzureDeployment.Should().Be("prod-gpt4o");
        stored.Settings.AzureApiVersion.Should().Be("2024-06-01");
    }

    // ─── helpers ────────────────────────────────────────────────────────────────────

    private static async Task SeedConfigAsync(LlmConfigApiFactory factory, string apiKey, string model)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantLlmConfigStore>();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new TenantLlmConfig
        {
            TenantId = EntityId.From(TenantId),
            ProviderType = ProviderType.OpenAiCompatible,
            Model = model,
            ApiKey = apiKey,
            ApiKeyLast4 = apiKey.Length >= 4 ? apiKey[^4..] : apiKey,
            Settings = new ProviderSettings { BaseUrl = "https://api.openai.com/v1" },
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        }, CancellationToken.None);
    }

    private static async Task<TenantLlmConfig?> LoadConfigAsync(LlmConfigApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantLlmConfigStore>();
        return await store.GetAsync(EntityId.From(TenantId), CancellationToken.None);
    }

    /// <summary>A canned <see cref="ILlmProvider"/> for the /test probe: completes or throws.</summary>
    private sealed class StubProvider : ILlmProvider
    {
        private readonly bool _succeed;
        private readonly Exception? _failure;

        public StubProvider(bool succeed, Exception? failure = null)
        {
            _succeed = succeed;
            _failure = failure;
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            if (_succeed)
                return Task.FromResult(new LlmResponse("pong"));
            throw _failure ?? new HttpRequestException("failed");
        }
    }

    /// <summary>
    /// Factory that grants <c>typification:ai:configure</c> (so the gate passes) and replaces
    /// <see cref="ILlmProviderResolver"/> with a substitute whose <c>BuildTransient</c> returns a
    /// per-test stub provider for the /test endpoint.
    /// </summary>
    private sealed class LlmConfigApiFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "test-api-key-12345";
        public const string TestTenantId = "tenant-test-001";
        public const string TestUserId = "test-admin-user";

        private static readonly string s_hashedKey = HashKey(TestApiKey);

        public ILlmProviderResolver Resolver { get; } = Substitute.For<ILlmProviderResolver>();

        /// <summary>The provider the substitute resolver's <c>BuildTransient</c> returns for /test.</summary>
        public ILlmProvider ProbeProvider { get; set; } = new StubProvider(succeed: true);

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                // Grant typification:ai:configure for the owning user id (the API-key user_id claim).
                var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
                if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);
                var roleStore = Substitute.For<IUserRoleStore>();
                var perms = new HashSet<string>(StringComparer.Ordinal) { "typification:ai:configure" };
                var empty = new HashSet<string>(StringComparer.Ordinal);
                roleStore.GetEffectivePermissionsAsync(
                        Arg.Any<TenantId>(),
                        Arg.Is<EntityId>(e => e.Value == TestUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlySet<string>>(perms));
                roleStore.GetEffectivePermissionsAsync(
                        Arg.Any<TenantId>(),
                        Arg.Is<EntityId>(e => e.Value != TestUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlySet<string>>(empty));
                services.AddSingleton(roleStore);

                // Replace the resolver so /test builds the per-test stub provider.
                Resolver.BuildTransient(Arg.Any<TenantLlmConfig>()).Returns(_ => ProbeProvider);
                services.AddSingleton(Resolver);
            });

            var host = base.CreateHost(builder);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);
            AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, TestTenantId);
            return host;
        }

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
            client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
            return client;
        }

        private static string HashKey(string rawKey)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
