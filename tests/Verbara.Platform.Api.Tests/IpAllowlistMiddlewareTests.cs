using System.Net;
using System.Security.Claims;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

public class IpAllowlistMiddlewareTests
{
    private const string TenantId = "t1";

    private static IHost BuildHost(
        ITenantIpAllowlistStore store,
        ITenantAuthConfigStore authConfigStore,
        IFeatureGateService featureGate,
        IAuditService audit,
        Action<HttpContext>? seedClaims = null)
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
                        services.AddSingleton(featureGate);
                        services.AddSingleton(audit);
                        services.AddSingleton<IIpAllowlistEvaluator, DefaultIpAllowlistEvaluator>();
                        services.AddRouting();
                        // Mirror Program.cs's ConfigureHttpJsonOptions so this isolated
                        // test host serializes Results.Ok(...) under the same
                        // source-gen-only contract as the Native AOT image (no
                        // reflection fallback). System.String lives in ApiJsonContext.
                        services.ConfigureHttpJsonOptions(o =>
                            o.SerializerOptions.TypeInfoResolverChain.Insert(
                                0, Serialization.ApiJsonContext.Default));
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.Use(async (ctx, next) =>
                        {
                            seedClaims?.Invoke(ctx);
                            await next();
                        });
                        app.UseMiddleware<IpAllowlistMiddleware>();
                        app.UseEndpoints(e =>
                        {
                            e.MapGet("/protected", () => Results.Ok("ok"));
                            e.MapGet("/anon", () => Results.Ok("ok")).AllowAnonymous();
                        });
                    });
            })
            .Build();
    }

    private static void SetClient(HttpContext ctx, string ip, string tenant, string? role = null)
    {
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var claims = new List<Claim> { new("tenant_id", tenant) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Should200_WhenAllowlistDisabled()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = false }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenIpInAllowlist()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "192.0.2.45", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should403_WhenIpNotInAllowlist()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should200_WhenPlatformAdminBypass()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId, role: "PlatformAdmin"));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenFeatureNotLicensed()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: false),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenAnonymousEndpoint()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService());
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/anon");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Stubs ────────────────────────────────────────────────────────────

    private sealed class StubFeatureGate(bool featureEnabled) : IFeatureGateService
    {
        public bool IsFeatureEnabled(string tenantId, PlanFeature feature) => featureEnabled;

        public IReadOnlySet<PlanFeature> GetEnabledFeatures(string tenantId) =>
            featureEnabled
                ? (IReadOnlySet<PlanFeature>)new HashSet<PlanFeature>(Enum.GetValues<PlanFeature>())
                : new HashSet<PlanFeature>();

        public int GetMaxChannels(string tenantId) => 100;
        public int GetAuditRetentionDays(string tenantId) => 90;
        public int GetMaxWebhookSubscriptions(string tenantId) => 10;
        public int GetMaxScheduledReports(string tenantId) => 10;
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task RecordAsync(
            TenantId tenantId, string category, string action, string severity,
            string actorId, string actorType, string? targetId = null, string? targetType = null,
            Guid? correlationId = null, AuditChanges? changes = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            DateTimeOffset? retainUntil = null,
            CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0618
        public Task LogAsync(TenantId tenantId, string action, string entityType, string entityId,
            string? performedBy = null, IReadOnlyDictionary<string, string>? details = null,
            CancellationToken ct = default) => Task.CompletedTask;
#pragma warning restore CS0618
    }
}
