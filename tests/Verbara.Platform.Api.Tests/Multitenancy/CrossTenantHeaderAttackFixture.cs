// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

using System.Net.Http.Headers;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// Reusable <see cref="WebApplicationFactory{Program}"/> for the Trigger 3
/// cross-tenant attack regressions (PREPUB-2026-05-09-MT-001, MFA-001 follow-on).
/// Seeds:
/// <list type="bullet">
///   <item><description>One <see cref="TenantType.Platform"/> host tenant
///   (<c>platform</c>) with one platform Admin.</description></item>
///   <item><description>Two <see cref="TenantType.Customer"/> tenants
///   (<c>acme</c>, <c>globex</c>) each with one local Admin.</description></item>
///   <item><description>One <see cref="TenantType.Partner"/> tenant
///   (<c>partner-zeta</c>) parenting one customer descendant
///   (<c>partner-zeta-customer</c>) with admins on both sides — used by the
///   Partner-cross-tenant control case + Phase 2 MFA-001 hierarchy probes.</description></item>
///   <item><description>One management-class API key bound to the platform
///   admin so the management-key bypass branch of the new tenant boundary
///   middleware can be probed.</description></item>
/// </list>
/// JWTs are minted via <see cref="JwtTokenService.GenerateAccessToken(User)"/>
/// so the principal carries the real <c>tid</c> claim and the JWT scheme
/// (<c>eyJ</c>-prefixed) is selected by the dynamic auth selector. This is the
/// codepath actually exploited by MT-001 — API keys overwrite
/// <c>Items["TenantId"]</c> in <c>ApiKeyAuthenticationHandler</c> and are NOT
/// vulnerable to the header-based escalation, so probing this surface with API
/// keys would yield false negatives.
/// </summary>
public sealed class CrossTenantHeaderAttackFixture : WebApplicationFactory<Program>
{
    public const string PlatformTenantId = "platform";
    public const string AcmeTenantId = "acme";
    public const string GlobexTenantId = "globex";
    public const string PartnerZetaTenantId = "partner-zeta";
    public const string PartnerZetaCustomerTenantId = "partner-zeta-customer";

    public const string PlatformAdminUserId = "platform-admin-user";
    public const string AcmeAdminUserId = "acme-admin-user";
    public const string GlobexAdminUserId = "globex-admin-user";
    public const string PartnerZetaAdminUserId = "partner-zeta-admin-user";
    public const string PartnerZetaCustomerAdminUserId = "partner-zeta-customer-admin-user";

    public const string ManagementApiKeyRaw = "mgmt-platform-key-mt001";

    private bool _seeded;
    private readonly Lock _seedLock = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
        });

        var host = base.CreateHost(builder);
        SeedHierarchy(host.Services);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, PlatformTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, AcmeTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, GlobexTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, PartnerZetaTenantId);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, PartnerZetaCustomerTenantId);
        return host;
    }

    private void SeedHierarchy(IServiceProvider services)
    {
        lock (_seedLock)
        {
            if (_seeded) return;
            _seeded = true;
        }

        using var scope = services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

        // Tenants
        Upsert(tenantStore, PlatformTenantId, "Platform Host", TenantType.Platform, parent: null);
        Upsert(tenantStore, AcmeTenantId, "Acme Inc", TenantType.Customer, parent: PlatformTenantId);
        Upsert(tenantStore, GlobexTenantId, "Globex Corp", TenantType.Customer, parent: PlatformTenantId);
        Upsert(tenantStore, PartnerZetaTenantId, "Partner Zeta", TenantType.Partner, parent: PlatformTenantId);
        Upsert(tenantStore, PartnerZetaCustomerTenantId, "Partner Zeta Customer", TenantType.Customer, parent: PartnerZetaTenantId);

        // Admins (one per tenant)
        SaveAdmin(userStore, PlatformAdminUserId, PlatformTenantId, "platform-admin@test.internal");
        SaveAdmin(userStore, AcmeAdminUserId, AcmeTenantId, "acme-admin@test.internal");
        SaveAdmin(userStore, GlobexAdminUserId, GlobexTenantId, "globex-admin@test.internal");
        SaveAdmin(userStore, PartnerZetaAdminUserId, PartnerZetaTenantId, "partner-zeta-admin@test.internal");
        SaveAdmin(userStore, PartnerZetaCustomerAdminUserId, PartnerZetaCustomerTenantId, "partner-zeta-customer-admin@test.internal");

        // Management API key bound to platform admin (for the management-key bypass branch test).
        var hashed = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ManagementApiKeyRaw)));
        apiKeyStore.SaveAsync(new ApiKey
        {
            KeyId = EntityId.From("mgmt-key-mt001"),
            TenantId = new TenantId(PlatformTenantId),
            Name = "Platform Mgmt MT-001",
            HashedKey = hashed,
            Scopes = ["platform:*"],
            UserId = EntityId.From(PlatformAdminUserId),
            KeyType = ApiKeyType.Management,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Upsert(ITenantStore store, string tenantId, string name, TenantType type, string? parent)
    {
        store.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = name,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = parent,
        }).AsTask().GetAwaiter().GetResult();
    }

    private static void SaveAdmin(IUserStore store, string userId, string tenantId, string email)
    {
        store.SaveAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = email,
            DisplayName = email,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Mints a real platform JWT (RS256) carrying <c>tid</c>, <c>sub</c>, role
    /// <c>Admin</c>) for the given user. The token is selected by the dynamic
    /// auth scheme via the <c>eyJ</c> prefix and validated by the JwtBearer
    /// handler exactly as a real client login token would be.
    /// </summary>
    public string MintAdminJwt(string userId, string tenantId, string email)
    {
        using var scope = Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtTokenService>();
        var user = new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = email,
            DisplayName = email,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var (token, _) = jwt.GenerateAccessToken(user);
        return token;
    }

    /// <summary>
    /// Convenience helpers for the four pre-seeded admins. A new client (and
    /// therefore a fresh JWT) is returned per call to keep tests independent.
    /// </summary>
    public HttpClient CreatePlatformAdminClient()
        => CreateBearerClient(MintAdminJwt(PlatformAdminUserId, PlatformTenantId, "platform-admin@test.internal"));

    public HttpClient CreateAcmeAdminClient()
        => CreateBearerClient(MintAdminJwt(AcmeAdminUserId, AcmeTenantId, "acme-admin@test.internal"));

    public HttpClient CreateGlobexAdminClient()
        => CreateBearerClient(MintAdminJwt(GlobexAdminUserId, GlobexTenantId, "globex-admin@test.internal"));

    public HttpClient CreatePartnerZetaAdminClient()
        => CreateBearerClient(MintAdminJwt(PartnerZetaAdminUserId, PartnerZetaTenantId, "partner-zeta-admin@test.internal"));

    /// <summary>
    /// HTTP client carrying the management API key as a bearer (api-key scheme).
    /// </summary>
    public HttpClient CreateManagementApiKeyClient()
        => CreateBearerClient(ManagementApiKeyRaw);

    private HttpClient CreateBearerClient(string bearer)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    // ─── Attack-shape helpers ────────────────────────────────────────────────

    /// <summary>
    /// MT-001 shape: send <c>X-Tenant-Id: headerTenantId</c> on a request whose
    /// authenticated principal carries <c>tid: jwtTid</c> for an unrelated
    /// tenant. This is the canonical pre-fix exploit.
    /// </summary>
    public static Task<HttpResponseMessage> SendWithHeaderOverrideAsync(
        HttpClient client, string headerTenantId, string path, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Tenant-Id", headerTenantId);
        return client.SendAsync(request, ct);
    }

    /// <summary>
    /// Subdomain-based override is OUT OF SCOPE for this fixture: the
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> client targets
    /// <c>localhost</c> and the <c>TenantResolutionMiddleware</c> short-circuits
    /// for hosts whose first label is <c>localhost</c>/<c>www</c>/<c>api</c>.
    /// Subdomain attacks are exercised by <c>SubdomainResolutionTests</c> via
    /// the dedicated <c>HostHeaderClient</c> shim; they are NOT re-tested here.
    /// </summary>
    public static void SendWithSubdomainOverride() =>
        throw new NotSupportedException(
            "Subdomain attack-shape probing is performed by SubdomainResolutionTests; " +
            "the WebApplicationFactory client only exercises the X-Tenant-Id header path. " +
            "Use SendWithHeaderOverrideAsync for MT-001 regressions.");

    /// <summary>
    /// Phase 2 MFA-001 shape: append <c>?targetTenant=overrideTenant</c> to a
    /// request whose principal owns a different tenant. Pre-positioned now so
    /// MFA-001 reuses the fixture without further plumbing.
    /// </summary>
    public static Task<HttpResponseMessage> SendWithTargetTenantQueryAsync(
        HttpClient client, string overrideTenant, string path, HttpMethod? method = null, CancellationToken ct = default)
    {
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{path}{separator}targetTenant={Uri.EscapeDataString(overrideTenant)}";
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        return client.SendAsync(request, ct);
    }
}
