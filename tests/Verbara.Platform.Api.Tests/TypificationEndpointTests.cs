using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Tests for <see cref="Endpoints.TypificationEndpoints"/>: schema CRUD + publish
/// validation + versioning rule, and binding create. The 402-license path is
/// exercised by <see cref="NoTypificationLicenseFactory"/> which mirrors
/// <see cref="AuthenticatedPlatformApiFactory"/>'s operational-tenant setup but
/// swaps the licensing substitute to <c>AddNoProFeaturesLicensed()</c>.
/// </summary>
public sealed class TypificationEndpointTests : IDisposable
{
    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public TypificationEndpointTests()
    {
        _factory = new AuthenticatedPlatformApiFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // A minimal but VALID published-able schema: one root non-leaf + one leaf child.
    private static object ValidSchemaBody(string name, int maxDepth = 5) => new
    {
        name,
        maxDepth,
        nodes = new object[]
        {
            new
            {
                nodeId = "root-1",
                parentNodeId = (string?)null,
                label = "Sales",
                code = "SALES",
                sortOrder = 0,
                isLeaf = false,
                channelApplicability = (string[]?)null,
                leaf = (object?)null,
            },
            new
            {
                nodeId = "leaf-1",
                parentNodeId = "root-1",
                label = "Closed Won",
                code = "CLOSED_WON",
                sortOrder = 0,
                isLeaf = true,
                channelApplicability = (string[]?)null,
                leaf = new
                {
                    category = "Success",
                    triggerRetry = false,
                    retryDelayMinutes = (int?)null,
                    triggerCallback = false,
                    dialerCode = (string?)null,
                    isActive = true,
                },
            },
        },
        fields = Array.Empty<object>(),
    };

    private static async Task<string> CreateSchemaAsync(HttpClient client, object body)
    {
        var response = await client.PostAsync("/api/admin/typification/schemas", JsonContent.Create(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return created!["schemaId"]!.GetValue<string>();
    }

    [Fact]
    public async Task PublishSchema_ShouldReturnOkFalseWithErrors_WhenDepthExceedsMax()
    {
        // maxDepth=1 but the tree has a root→leaf chain of depth 2 → ValidateForPublish fails.
        // Validation failures return 200 with { ok: false, errors } so the Web onSuccess fires.
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("Depth Violation", maxDepth: 1));

        var response = await _client.PostAsync($"/api/admin/typification/schemas/{id}/publish", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        body!["ok"]!.GetValue<bool>().Should().BeFalse();
        body["errors"]!.AsArray().Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PublishSchema_ShouldBumpToPublished_WhenValid()
    {
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("Publishable"));

        var publishResponse = await _client.PostAsync($"/api/admin/typification/schemas/{id}/publish", content: null);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var publishBody = JsonNode.Parse(await publishResponse.Content.ReadAsStringAsync());
        publishBody!["ok"]!.GetValue<bool>().Should().BeTrue();

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        schema!["isPublished"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task PutSchema_ShouldUpdateDraftInPlace_WhenLatestUnpublished()
    {
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("Draft Edit"));

        var update = ValidSchemaBody("Draft Edit Renamed");
        var response = await _client.PutAsync($"/api/admin/typification/schemas/{id}", JsonContent.Create(update));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        updated!["version"]!.GetValue<int>().Should().Be(1, because: "an unpublished draft is edited in place");
        updated["name"]!.GetValue<string>().Should().Be("Draft Edit Renamed");
        updated["isPublished"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task PutSchema_ShouldCreateNewVersion_WhenLatestPublished()
    {
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("Versioned"));

        var publishResponse = await _client.PostAsync($"/api/admin/typification/schemas/{id}/publish", content: null);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = ValidSchemaBody("Versioned v2");
        var response = await _client.PutAsync($"/api/admin/typification/schemas/{id}", JsonContent.Create(update));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        updated!["version"]!.GetValue<int>().Should().Be(2, because: "editing a published version forks a new draft version");
        updated["isPublished"]!.GetValue<bool>().Should().BeFalse(because: "the forked version starts as a draft");
    }

    [Fact]
    public async Task CreateBinding_ShouldPersist_WhenValid()
    {
        var schemaId = await CreateSchemaAsync(_client, ValidSchemaBody("Bound Schema"));

        var body = new
        {
            scope = "Tenant",
            scopeRef = (string?)null,
            schemaId,
            subtreeRootNodeId = (string?)null,
            priority = 10,
        };
        var response = await _client.PostAsync("/api/admin/typification/bindings", JsonContent.Create(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var bindingId = created!["bindingId"]!.GetValue<string>();
        created["scope"]!.GetValue<string>().Should().Be("Tenant");
        created["schemaId"]!.GetValue<string>().Should().Be(schemaId);

        var getResponse = await _client.GetAsync($"/api/admin/typification/bindings/{bindingId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateSchema_ShouldPersistFieldPrefillSource_WhenProvided()
    {
        var body = SchemaBodyWithField("Prefilled", new
        {
            kind = "Metadata",
            @ref = "patientId",
        });

        var id = await CreateSchemaAsync(_client, body);

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        var field = schema!["fields"]!.AsArray()[0]!;
        var prefill = field["prefillSource"]!;
        prefill["kind"]!.GetValue<string>().Should().Be("Metadata");
        prefill["ref"]!.GetValue<string>().Should().Be("patientId");
    }

    [Fact]
    public async Task CreateSchema_ShouldLeaveFieldPrefillSourceNull_WhenOmitted()
    {
        var body = SchemaBodyWithField("NoPrefill", prefillSource: null);

        var id = await CreateSchemaAsync(_client, body);

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        var field = schema!["fields"]!.AsArray()[0]!;
        field["prefillSource"].Should().BeNull();
    }

    // A schema with a single global field; the field's prefillSource is supplied by
    // the caller (null = omitted) to exercise the C9b round-trip both ways.
    private static object SchemaBodyWithField(string name, object? prefillSource) => new
    {
        name,
        maxDepth = 5,
        nodes = Array.Empty<object>(),
        fields = new object[]
        {
            new
            {
                fieldId = "field-1",
                key = "patient_id",
                label = "Patient ID",
                type = "Text",
                required = false,
                options = (object[]?)null,
                validation = (object?)null,
                attachToNodeId = (string?)null,
                visibleWhen = (object?)null,
                prefillSource,
                sortOrder = 0,
            },
        },
    };

    [Fact]
    public async Task CreateSchema_ShouldReturn402_WhenAdvancedTypificationMissing()
    {
        // NoTypificationLicenseFactory reports NO licensed features, so the
        // RequireLicenseFeature(AdvancedTypification) gate must respond 402.
        using var factory = new NoTypificationLicenseFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/admin/typification/schemas",
            JsonContent.Create(ValidSchemaBody("Unlicensed")));

        response.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
    }

    // ─── D3 — AiConfig admin DTO round-trip ──────────────────────────────────────

    [Fact]
    public async Task CreateSchema_ShouldPersistAiConfig_WhenProvided()
    {
        var body = SchemaBodyWithAiConfig("With AI", new
        {
            enabled = true,
            mode = "SuggestOnly",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = false,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        });

        var id = await CreateSchemaAsync(_client, body);

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        var ai = schema!["aiConfig"]!;
        ai["enabled"]!.GetValue<bool>().Should().BeTrue();
        ai["mode"]!.GetValue<string>().Should().Be("SuggestOnly");
        ai["suggestThreshold"]!.GetValue<double>().Should().Be(0.7);
        ai["autoApplyThreshold"]!.GetValue<double>().Should().Be(0.85);
        ai["autonomousThreshold"]!.GetValue<double>().Should().Be(0.95);
        ai["autonomous"]!.GetValue<bool>().Should().BeFalse();
        ai["sentimentGating"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CreateSchema_ShouldDefaultAiConfigDisabled_WhenOmitted()
    {
        // ValidSchemaBody carries no aiConfig → server defaults to disabled (Mode=Off).
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("No AI"));

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        var ai = schema!["aiConfig"]!;
        ai["enabled"]!.GetValue<bool>().Should().BeFalse();
        ai["mode"]!.GetValue<string>().Should().Be("Off", because: "AiMode.Off is the new default (0)");
        ai["sentimentGating"]!.GetValue<bool>().Should().BeTrue(because: "SentimentGating defaults true");
    }

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.5, 0.0)]
    public async Task CreateSchema_ShouldClampSuggestThreshold_WhenOutOfRange(
        double submitted, double expected)
    {
        // An admin must not be able to persist a threshold outside [0, 1]: > 1 would
        // suppress every suggestion, < 0 would never suppress. MapAiConfig clamps.
        var body = SchemaBodyWithAiConfig($"Clamp {submitted}", new
        {
            enabled = true,
            mode = "SuggestOnly",
            suggestThreshold = submitted,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = false,
            sentimentGating = false,
            dailyTokenBudget = (long?)null,
        });

        var id = await CreateSchemaAsync(_client, body);

        var getResponse = await _client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        schema!["aiConfig"]!["suggestThreshold"]!.GetValue<double>().Should().Be(expected);
    }

    // ─── C4 — calibration-status endpoint + AutoFill/autonomous write guards ──────

    [Fact]
    public async Task GetCalibrationStatus_ShouldReturn403_WhenCallerLacksConfigurePermission()
    {
        // Default factory: API-key admin with NO seeded role assignments, so
        // PermissionResolver.ResolveAsync yields an empty permission set.
        var id = await CreateSchemaAsync(_client, ValidSchemaBody("No Perm"));

        var response = await _client.GetAsync(
            $"/api/admin/typification/schemas/{id}/calibration-status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCalibrationStatus_ShouldReturnNotReadyStatus_WhenCallerHasPermissionAndNoSamples()
    {
        using var factory = new ConfigurePermissionTypificationFactory();
        var client = factory.CreateAuthenticatedClient();

        var id = await CreateSchemaAsync(client, ValidSchemaBody("Calib Ready Perm"));

        var response = await client.GetAsync(
            $"/api/admin/typification/schemas/{id}/calibration-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        status!["samples"]!.GetValue<int>().Should().Be(0);
        status["accuracy"]!.GetValue<double>().Should().Be(0);
        status["autoFillReady"]!.GetValue<bool>().Should().BeFalse();
        status["autonomousReady"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task CreateSchema_ShouldReject_WhenModeIsAutoFillWithoutCalibration()
    {
        // A brand-new schema has no published calibration, so AutoFill must be rejected
        // at write time (defense-in-depth mirror of the runtime band gate).
        var body = SchemaBodyWithAiConfig("AutoFill Premature", new
        {
            enabled = true,
            mode = "AutoFill",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = false,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        });

        var response = await _client.PostAsync(
            "/api/admin/typification/schemas", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        error!["ok"]!.GetValue<bool>().Should().BeFalse();
        var firstError = error["errors"]!.AsArray()[0]!;
        firstError["field"]!.GetValue<string>().Should().Be("aiConfig.mode");
        firstError["message"]!.GetValue<string>().Should().Contain("calibration");
    }

    [Fact]
    public async Task CreateSchema_ShouldReject_WhenAutonomousWithoutPermission()
    {
        // Default factory has no typification:ai:autonomous permission. Keep mode
        // SuggestOnly so only the autonomous gate is exercised (not the AutoFill gate).
        var body = SchemaBodyWithAiConfig("Autonomous No Perm", new
        {
            enabled = true,
            mode = "SuggestOnly",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = true,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        });

        var response = await _client.PostAsync(
            "/api/admin/typification/schemas", JsonContent.Create(body));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateSchema_ShouldPersistAutonomous_WhenCallerHasAutonomousPermission()
    {
        // The perms-granting factory resolves typification:ai:autonomous for the owning
        // user id (the API-key `user_id` claim). This proves the autonomous gate is a real
        // gate that opens for an authorized caller — NOT a hard deny — and is the exact test
        // that would have caught C-1 (which resolved the key id → empty perms → 403).
        using var factory = new ConfigurePermissionTypificationFactory();
        var client = factory.CreateAuthenticatedClient();

        var body = SchemaBodyWithAiConfig("Autonomous Authorized", new
        {
            enabled = true,
            mode = "SuggestOnly",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = true,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        });

        var id = await CreateSchemaAsync(client, body);

        var getResponse = await client.GetAsync($"/api/admin/typification/schemas/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var schema = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync());
        schema!["aiConfig"]!["autonomous"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSchema_ShouldReject_WhenModeChangedToAutoFillWithoutCalibration()
    {
        // Create a SuggestOnly schema (allowed), then PUT it switching mode to AutoFill.
        // The Update path re-checks the calibration bar (distinct from Create) and must
        // reject — the anti-bypass guarantee that AutoFill can't be smuggled in via update.
        var id = await CreateSchemaAsync(_client, SchemaBodyWithAiConfig("Update To AutoFill", new
        {
            enabled = true,
            mode = "SuggestOnly",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = false,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        }));

        var updateBody = SchemaBodyWithAiConfig("Update To AutoFill", new
        {
            enabled = true,
            mode = "AutoFill",
            suggestThreshold = 0.7,
            autoApplyThreshold = 0.85,
            autonomousThreshold = 0.95,
            autonomous = false,
            sentimentGating = true,
            dailyTokenBudget = (long?)null,
        });

        var response = await _client.PutAsync(
            $"/api/admin/typification/schemas/{id}", JsonContent.Create(updateBody));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        error!["ok"]!.GetValue<bool>().Should().BeFalse();
        var firstError = error["errors"]!.AsArray()[0]!;
        firstError["field"]!.GetValue<string>().Should().Be("aiConfig.mode");
        firstError["message"]!.GetValue<string>().Should().Contain("calibration");
    }

    // ValidSchemaBody plus an aiConfig block (the only AI-bearing variant of the body).
    private static object SchemaBodyWithAiConfig(string name, object aiConfig) => new
    {
        name,
        maxDepth = 5,
        nodes = new object[]
        {
            new
            {
                nodeId = "root-1",
                parentNodeId = (string?)null,
                label = "Sales",
                code = "SALES",
                sortOrder = 0,
                isLeaf = false,
                channelApplicability = (string[]?)null,
                leaf = (object?)null,
            },
            new
            {
                nodeId = "leaf-1",
                parentNodeId = "root-1",
                label = "Closed Won",
                code = "CLOSED_WON",
                sortOrder = 0,
                isLeaf = true,
                channelApplicability = (string[]?)null,
                leaf = new
                {
                    category = "Success",
                    triggerRetry = false,
                    retryDelayMinutes = (int?)null,
                    triggerCallback = false,
                    dialerCode = (string?)null,
                    isActive = true,
                },
            },
        },
        fields = Array.Empty<object>(),
        aiConfig,
    };
}

/// <summary>
/// Variant of <see cref="AuthenticatedPlatformApiFactory"/> that keeps the
/// operational Customer tenant + admin auth wiring (so RequireOperationalTenant
/// and AdminOnly pass) but reports NO licensed Pro features via
/// <c>AddNoProFeaturesLicensed()</c> — driving the LicenseGate 402 path for the
/// AdvancedTypification-gated typification endpoints.
/// </summary>
public sealed class NoTypificationLicenseFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";
    public const string TestUserId = "test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            // The only difference vs AuthenticatedPlatformApiFactory: no licensed features.
            services.AddNoProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
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

/// <summary>
/// Variant of <see cref="AuthenticatedPlatformApiFactory"/> whose test caller resolves the
/// <c>typification:ai:configure</c> AND <c>typification:ai:autonomous</c> permissions. The
/// default factory's API-key admin has no seeded role assignments (empty effective
/// permissions); here the <see cref="IUserRoleStore"/> is replaced with a substitute that
/// grants those permissions ONLY when the resolved caller id equals the owning user id
/// (<see cref="AuthenticatedPlatformApiFactory.TestUserId"/>) — and an empty set for any
/// other id. That id-specific stub is the tripwire for C-1: if the endpoint regresses to
/// resolving the API-key id (NameIdentifier) instead of the owning-user <c>user_id</c>
/// claim, the granted permissions no longer match and every AI-config gate 403s.
/// </summary>
public sealed class ConfigurePermissionTypificationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestTenantId = "tenant-test-001";
    public const string TestUserId = "test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

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

            // Grant typification:ai:configure + typification:ai:autonomous via IUserRoleStore
            // so PermissionResolver.ResolveAsync returns them — but ONLY for the owning user id
            // (TestUserId, carried in the API-key `user_id` claim). Any other id (e.g. the
            // API-key id NameIdentifier) gets an empty set: this is the C-1 tripwire.
            var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
            if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);

            var roleStore = Substitute.For<IUserRoleStore>();
            var perms = new HashSet<string>(StringComparer.Ordinal)
            {
                "typification:ai:configure",
                "typification:ai:autonomous",
            };
            var empty = new HashSet<string>(StringComparer.Ordinal);
            roleStore.GetEffectivePermissionsAsync(
                    Arg.Any<TenantId>(),
                    Arg.Is<EntityId>(e => e.Value == AuthenticatedPlatformApiFactory.TestUserId),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(perms));
            roleStore.GetEffectivePermissionsAsync(
                    Arg.Any<TenantId>(),
                    Arg.Is<EntityId>(e => e.Value != AuthenticatedPlatformApiFactory.TestUserId),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(empty));
            services.AddSingleton(roleStore);
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
