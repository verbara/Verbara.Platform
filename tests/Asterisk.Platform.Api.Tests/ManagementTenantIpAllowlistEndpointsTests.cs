using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementTenantIpAllowlistEndpointsTests
{
    private const string TenantId = "tenant-test";
    private const string BasePath = $"/api/v1/management/tenants/{TenantId}/ip-allowlist";

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

        var response = await client.PostAsJsonAsync(BasePath, new { cidr = "10.0.0.0/8", description = "office" });

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

        var response = await client.PostAsJsonAsync(BasePath, new { cidr = "not-a-cidr", description = (string?)null });

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
