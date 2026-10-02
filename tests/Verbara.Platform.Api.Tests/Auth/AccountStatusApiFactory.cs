using System.Net.Http.Headers;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Test host for the account-status suites: real in-memory stores (users, refresh tokens, audit,
/// auth events, tenants), real JWTs minted by the host's <see cref="JwtTokenService"/>, a known
/// <c>Services:ServiceKey</c> so the <c>/api/v1/internal</c> surface Realtime calls is reachable,
/// and — the one Verbara hosted service it keeps — the production-registered
/// <see cref="UserAccessRevocationListener"/>, so a status change ends open SSE streams end to end.
/// </summary>
/// <remarks>
/// Every user holds <c>platform:tenant:impersonate</c> (stubbed <see cref="IUserRoleStore"/>, the same
/// approach as <c>HierarchyImpersonationFactory</c>) so an impersonation refusal can only come from
/// the account-status gate, never from the permission check that precedes it.
/// </remarks>
public class AccountStatusApiFactory : WebApplicationFactory<Program>
{
    public const string PlatformTenantId = "acct-status-platform";
    public const string CustomerTenantId = "acct-status-customer";
    public const string PlatformAdminUserId = "acct-status-platform-admin";
    public const string CustomerAdminUserId = "acct-status-customer-admin";
    public const string ServiceKey = "acct-status-service-key-0123456789abcdef";

    private static readonly HashSet<string> s_grantedPermissions = new(StringComparer.Ordinal)
    {
        "platform:tenant:impersonate",
        "users:user:view",
        "users:user:edit",
        "contacts:contact:view",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Services:ServiceKey", ServiceKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ServiceKey"] = ServiceKey,
        }));

        builder.ConfigureServices(services =>
        {
            // StubVerbaraHostedServices strips every Verbara hosted service, the revocation listener
            // included — and without it nothing ends an open SSE stream on user.access_revoked. Keep
            // the descriptors Program.cs itself registered (never a test-made copy), so a host that
            // stops registering the listener fails the end-to-end stream tests.
            var revocationListeners = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(UserAccessRevocationListener))
                .ToList();
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            foreach (var listener in revocationListeners)
                services.Add(listener);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            foreach (var d in services.Where(d => d.ServiceType == typeof(IUserRoleStore)).ToList())
                services.Remove(d);
            var roleStore = Substitute.For<IUserRoleStore>();
            roleStore.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(s_grantedPermissions));
            services.AddSingleton(roleStore);

            ConfigureTestServices(services);
        });

        var host = base.CreateHost(builder);
        Seed(host.Services);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, PlatformTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, CustomerTenantId);
        return host;
    }

    /// <summary>A derived host's last word on its services, after this one has configured its own.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    private static void Seed(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        UpsertTenant(tenants, PlatformTenantId, TenantType.Platform, parent: null);
        UpsertTenant(tenants, CustomerTenantId, TenantType.Customer, parent: PlatformTenantId);

        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        SaveUser(users, PlatformAdminUserId, PlatformTenantId, UserRole.Admin, UserStatus.Active);
        SaveUser(users, CustomerAdminUserId, CustomerTenantId, UserRole.Admin, UserStatus.Active);
    }

    private static void UpsertTenant(ITenantStore store, string tenantId, TenantType type, string? parent) =>
        store.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = parent,
        }).AsTask().GetAwaiter().GetResult();

    private static User SaveUser(
        IUserStore store, string userId, string tenantId, UserRole role, UserStatus status, string? password = null)
    {
        var user = new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = $"{userId}@acct-status.test",
            DisplayName = userId,
            Role = role,
            Status = status,
            PasswordHash = password is null ? null : PasswordService.HashPassword(password),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        store.SaveAsync(user, CancellationToken.None).GetAwaiter().GetResult();
        return user;
    }

    /// <summary>
    /// Persists a user (any status) through the host's real <see cref="IUserStore"/>, with a password
    /// it can sign in with when <paramref name="password"/> is given.
    /// </summary>
    public User SaveUser(string userId, string tenantId, UserRole role, UserStatus status, string? password = null)
    {
        using var scope = Services.CreateScope();
        return SaveUser(scope.ServiceProvider.GetRequiredService<IUserStore>(), userId, tenantId, role, status, password);
    }

    /// <summary>Loads a user through the host's <see cref="IUserStore"/>.</summary>
    public User? GetUser(string userId, string tenantId)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IUserStore>()
            .GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Mints an access token for <paramref name="user"/> exactly as sign-in would — the token
    /// carries whatever role the user had at issuance, independent of its status today.
    /// </summary>
    public string MintAccessToken(User user)
    {
        using var scope = Services.CreateScope();
        var (token, _) = scope.ServiceProvider.GetRequiredService<JwtTokenService>().GenerateAccessToken(user);
        return token;
    }

    public HttpClient CreateBearerClient(string bearer)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    public HttpClient CreateServiceClient(string? serviceKey = ServiceKey)
    {
        var client = CreateClient();
        if (serviceKey is not null)
            client.DefaultRequestHeaders.Add("X-Service-Key", serviceKey);
        return client;
    }
}
