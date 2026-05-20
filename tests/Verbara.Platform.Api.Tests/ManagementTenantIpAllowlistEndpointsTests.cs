using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

public sealed class ManagementTenantIpAllowlistEndpointsTests
{
    private const string TenantId = "tenant-test";
    private const string BasePath = $"/api/v1/management/tenants/{TenantId}/ip-allowlist";

    // Mirror Program.cs's ConfigureHttpJsonOptions so this isolated test host runs
    // under the same source-gen-only JSON contract as the Native AOT image (no
    // reflection fallback once JsonSerializerIsReflectionEnabledByDefault=false).
    private static void ConfigureAotJson(IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolverChain.Insert(
                0, Serialization.ApiJsonContext.Default));

    private static IHost BuildHost(
        ITenantIpAllowlistStore store,
        ITenantAuthConfigStore authConfigStore)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(store);
                        services.AddSingleton(authConfigStore);
                        services.AddSingleton<IAuditService>(new NoopAuditService());
                        services.AddRouting();
                        ConfigureAotJson(services);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e =>
                        {
                            e.MapGet(
                                "/api/v1/management/tenants/{tenantId}/ip-allowlist",
                                ManagementTenantIpAllowlistEndpoints.List);
                            e.MapPost(
                                "/api/v1/management/tenants/{tenantId}/ip-allowlist",
                                ManagementTenantIpAllowlistEndpoints.Add);
                            e.MapDelete(
                                "/api/v1/management/tenants/{tenantId}/ip-allowlist/{entryId:guid}",
                                ManagementTenantIpAllowlistEndpoints.Remove);
                        });
                    });
            })
            .Build();
    }

    [Fact]
    public async Task Post_ShouldReturn201_OnValidCidr()
    {
        var store = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();

        using var host = BuildHost(store, authConfig);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            BasePath, new AddIpAllowlistEntryRequest("10.0.0.0/8", "office"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("10.0.0.0/8");
    }

    [Fact]
    public async Task Post_ShouldReturn400_OnMalformedCidr()
    {
        var store = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();

        using var host = BuildHost(store, authConfig);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            BasePath, new AddIpAllowlistEntryRequest("not-a-cidr", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ip_allowlist_invalid_cidr");
    }

    [Fact]
    public async Task Get_ShouldListEntries()
    {
        var store = new InMemoryTenantIpAllowlistStore();
        await store.AddAsync(TenantId, "192.168.1.0/24", "vpn", null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(store, authConfig);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync(BasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("192.168.1.0/24");
        body.Should().Contain("\"enabled\":true");
    }

    [Fact]
    public async Task Delete_ShouldRemoveEntry()
    {
        var store = new InMemoryTenantIpAllowlistStore();
        var entry1 = await store.AddAsync(TenantId, "10.0.0.0/8", null, null, default);
        await store.AddAsync(TenantId, "172.16.0.0/12", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(store, authConfig);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.DeleteAsync($"{BasePath}/{entry1.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var remaining = await store.ListAsync(TenantId, default);
        remaining.Should().HaveCount(1);
    }

    [Fact]
    public async Task EnableAllowlist_ShouldReturn400_WhenNoEntries()
    {
        // Arrange — empty allowlist store, no entries for the tenant
        var store = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = false }, default);

        var requestBody = new UpdateTenantSettingsRequest(
            Auth: new UpdateAuthSettingsDto(IpAllowlistEnabled: true));

        // Build a minimal service provider with the required services
        var services = new ServiceCollection();
        services.AddSingleton<ITenantIpAllowlistStore>(store);
        services.AddSingleton<IAuditService>(new NoopAuditService());
        var sp = services.BuildServiceProvider();

        // Act — invoke ApplyUpdates directly (focused §4.1 validation test)
        var result = await TenantSettingsEndpoints.ApplyUpdates(
            tenantId: TenantId,
            body: requestBody,
            tenantStore: new InMemoryTenantStore(),
            authConfigStore: authConfig,
            quotaStore: new InMemoryTenantQuotaStore(),
            retentionStore: new InMemoryTenantRetentionPolicyStore(),
            tierCache: null,
            featureGateCache: null,
            addOnStore: null,
            brandingStore: null,
            ct: default,
            sp: sp,
            actorName: "test-actor");

        // Assert — ApplyUpdates returns non-null IResult (the 400) when §4.1 is violated
        result.Should().NotBeNull();

        // Verify the result is a 400 with the expected error code by executing via a test host
        using var host = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        ConfigureAotJson(services);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(e =>
                        {
                            e.MapGet("/test", async ctx =>
                            {
                                await result!.ExecuteAsync(ctx);
                            });
                        });
                    });
            })
            .Build();
        await host.StartAsync();
        var response = await host.GetTestClient().GetAsync("/test");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("ip_allowlist_enable_requires_entries");
    }

    [Fact]
    public async Task Delete_ShouldReturn400_WhenLastEntryAndEnabled()
    {
        var store = new InMemoryTenantIpAllowlistStore();
        var entry = await store.AddAsync(TenantId, "10.0.0.0/8", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(store, authConfig);
        await host.StartAsync();
        var client = host.GetTestClient();

        var response = await client.DeleteAsync($"{BasePath}/{entry.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ip_allowlist_cannot_empty_while_enabled");
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task RecordAsync(
            TenantId tenantId, string category, string action, string severity,
            string actorId, string actorType, string? targetId = null, string? targetType = null,
            Guid? correlationId = null, AuditChanges? changes = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0618
        public Task LogAsync(TenantId tenantId, string action, string entityType, string entityId,
            string? performedBy = null, IReadOnlyDictionary<string, string>? details = null,
            CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618
    }
}
