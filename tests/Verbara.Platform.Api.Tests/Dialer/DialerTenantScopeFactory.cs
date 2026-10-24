using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dnc-and-dispositions-are-tenant-scoped: the real Api <c>Program</c> with two Customer tenants (<c>acme</c>,
/// <c>globex</c>, Enterprise plan) each with one administrator authenticated by a real JWT, and optionally the host
/// tenant. Everything else is as in <see cref="DialerHostWiringFactory"/>: the Verbara hosted services stripped, AMI
/// and the server stubbed, the in-memory stores, the licensing stubs.
/// </summary>
/// <remarks>
/// With a dialer connection string (a Testcontainers Postgres) <c>Program.cs</c> registers the dialer over it, and
/// this host keeps the <see cref="DncListStoreBase"/> and <see cref="CampaignStoreBase"/> descriptors
/// <c>UsePostgresDialerStorage</c> registered, re-adding them after
/// <see cref="AuthenticatedPlatformApiFactory.RegisterInMemoryStores"/>, so the routes run against Pro's Postgres
/// stores. Without one, the in-memory stores answer, which follow the Pro <c>2.20.3-pro</c> contract.
/// </remarks>
public sealed class DialerTenantScopeFactory : WebApplicationFactory<Program>
{
    public const string HostTenantId = "platform";
    public const string AcmeTenantId = "acme";
    public const string GlobexTenantId = "globex";

    private readonly string? _dialerConnectionString;
    private readonly bool _seedHostTenant;

    /// <summary>Creates the host with the in-memory dialer stores and the host tenant.</summary>
    public DialerTenantScopeFactory()
        : this(null)
    {
    }

    /// <summary>Creates the host.</summary>
    /// <param name="dialerConnectionString">The Testcontainers Postgres the dialer stores use, or
    /// <see langword="null"/> for the in-memory stores.</param>
    /// <param name="seedHostTenant">Whether the host tenant exists, as after <c>/api/setup</c>.</param>
    internal DialerTenantScopeFactory(string? dialerConnectionString, bool seedHostTenant = true)
    {
        _dialerConnectionString = dialerConnectionString;
        _seedHostTenant = seedHostTenant;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_dialerConnectionString is not null)
            builder.UseSetting("ConnectionStrings:Dialer", _dialerConnectionString);
    }

    /// <inheritdoc />
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            var postgresStores = services
                .Where(d => d.ServiceType == typeof(DncListStoreBase) || d.ServiceType == typeof(CampaignStoreBase))
                .ToList();

            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            foreach (var store in postgresStores)
                services.Add(store);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);
        Seed(host.Services);
        return host;
    }

    /// <summary>An HTTP client authenticated as <paramref name="tenantId"/>'s administrator.</summary>
    public HttpClient CreateAdminClient(string tenantId)
    {
        using var scope = Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var (token, _) = jwt.GenerateAccessToken(Admin(tenantId));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private void Seed(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();

        if (_seedHostTenant)
            Upsert(tenants, HostTenantId, TenantType.Platform, parent: null);
        foreach (var tenantId in new[] { AcmeTenantId, GlobexTenantId })
        {
            Upsert(tenants, tenantId, TenantType.Customer, parent: _seedHostTenant ? HostTenantId : null);
            users.CreateAsync(Admin(tenantId), CancellationToken.None).GetAwaiter().GetResult();
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(services, tenantId);
        }
    }

    private static void Upsert(ITenantStore store, string tenantId, TenantType type, string? parent) =>
        store.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = parent,
            Metadata = new Dictionary<string, string>
            {
                ["Plan"] = TenantPlan.Enterprise.ToString(),
                ["RateLimitTier"] = "Enterprise",
            },
        }).AsTask().GetAwaiter().GetResult();

    private static User Admin(string tenantId) => new()
    {
        UserId = EntityId.From($"{tenantId}-admin"),
        TenantId = new TenantId(tenantId),
        Email = $"{tenantId}-admin@test.internal",
        DisplayName = $"{tenantId} admin",
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
