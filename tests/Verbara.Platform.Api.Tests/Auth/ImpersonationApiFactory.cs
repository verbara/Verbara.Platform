using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The host for impersonation suites: <see cref="AccountStatusApiFactory"/>'s real in-memory stores
/// and real JWTs, every permission those suites rely on granted explicitly, and helpers that drive a
/// session through the real endpoints — start it, use its token, end it, revoke it, let it time out.
/// </summary>
/// <remarks>
/// <para>
/// Derived rather than edited: the base's own suites depend on exactly the grants it stubs. Nothing
/// here rests on the <c>Admin</c> role satisfying a permission check on its own; every permission a
/// test exercises is in <see cref="GrantedPermissions"/>.
/// </para>
/// <para>
/// The impersonation session store is in-memory and caps concurrent sessions per actor tenant, so a
/// suite that leaves sessions open must close them (<see cref="CloseSessionsAsync"/>).
/// </para>
/// </remarks>
public class ImpersonationApiFactory : AccountStatusApiFactory
{
    /// <summary>
    /// What every user of this host holds (stubbed <see cref="IUserRoleStore"/>):
    /// <c>platform:tenant:impersonate</c> starts a session, <c>system:impersonation:manage</c> lists and
    /// revokes sessions, and <c>users:user:view</c> is what the control request
    /// (<see cref="ListUsersAsync"/>) needs — a full impersonation token carries every non-platform grant.
    /// <c>billing:credits:read</c> (a read) and <c>features:agent-assist:manage</c> (a manage permission)
    /// show which grants a read-only session keeps.
    /// </summary>
    public static readonly IReadOnlySet<string> GrantedPermissions = new HashSet<string>(StringComparer.Ordinal)
    {
        "platform:tenant:impersonate",
        "system:impersonation:manage",
        "users:user:view",
        "users:user:edit",
        "contacts:contact:view",
        "billing:credits:read",
        "features:agent-assist:manage",
    };

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        services.RemoveAll<IUserRoleStore>();
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GrantedPermissions));
        services.AddSingleton(roles);
    }

    /// <summary>
    /// A second replica of this host. It shares what replicas share in production — the jti
    /// revocation store (Redis there) and the token signing key — and keeps its own impersonation
    /// session store, which is in-memory and per replica. Its tenants and seeded users are created
    /// as this host's are; a user added later goes to both through <see cref="NewPlatformAdmin"/>.
    /// </summary>
    public WebApplicationFactory<Program> CreateReplica()
    {
        var revocations = Services.GetRequiredService<IJtiRevocationCache>();
        var signing = Services.GetRequiredService<JwtTokenService>();
        return WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IJtiRevocationCache>();
            services.AddSingleton(revocations);
            services.RemoveAll<JwtTokenService>();
            services.AddSingleton(signing);
        }));
    }

    /// <summary>A new Active Admin of the platform tenant, created on this host and on every replica given.</summary>
    public User NewPlatformAdmin(params WebApplicationFactory<Program>[] replicas) =>
        CreateUser(NewUser($"impersonating-admin-{Guid.NewGuid():N}", PlatformTenantId), replicas);

    /// <summary>
    /// A Partner tenant of its own, with one Customer child and an Active Admin, on this host and on
    /// every replica given. Sessions its admin starts have that Partner as their actor tenant, whose
    /// impersonation timeout is <paramref name="impersonationTimeoutMinutes"/> — no other suite's
    /// session shares it, so a sweep that times out its sessions touches nothing else.
    /// </summary>
    public async Task<PartnerActor> NewPartnerActorAsync(
        int impersonationTimeoutMinutes, params WebApplicationFactory<Program>[] replicas)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var partnerTenantId = $"imp-partner-{suffix}";
        var customerTenantId = $"imp-customer-{suffix}";
        var admin = NewUser($"imp-partner-admin-{suffix}", partnerTenantId);

        foreach (var host in Hosts(replicas))
        {
            using var scope = host.Services.CreateScope();
            var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
            await tenants.UpsertAsync(NewTenant(partnerTenantId, TenantType.Partner, PlatformTenantId));
            await tenants.UpsertAsync(NewTenant(customerTenantId, TenantType.Customer, partnerTenantId));
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, partnerTenantId);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, customerTenantId);
            await scope.ServiceProvider.GetRequiredService<ITenantAuthConfigStore>().SaveAsync(
                new TenantAuthConfig
                {
                    TenantId = partnerTenantId,
                    ImpersonationAutoTimeoutMinutes = impersonationTimeoutMinutes,
                },
                CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUserStore>().CreateAsync(admin, CancellationToken.None);
        }

        return new PartnerActor(admin, customerTenantId);
    }

    /// <summary>
    /// Starts an impersonation of <paramref name="targetTenantId"/> as <paramref name="admin"/> through
    /// <c>POST /management/impersonate</c> on <paramref name="host"/> (this host when omitted).
    /// </summary>
    public async Task<StartedImpersonation> StartImpersonationAsync(
        User admin,
        string targetTenantId = CustomerTenantId,
        bool readOnly = false,
        WebApplicationFactory<Program>? host = null)
    {
        using var client = BearerClient(host, MintAccessToken(admin));
        using var response = await client.PostAsJsonAsync(
            "/api/v1/management/impersonate", new { targetTenantId, readOnly });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the impersonation must start ({body})");

        using var json = JsonDocument.Parse(body);
        return new StartedImpersonation(
            ReadString(json.RootElement, "accessToken"),
            ReadString(json.RootElement, "sessionId"));
    }

    /// <summary>The control request: <c>GET /admin/users</c> with <paramref name="bearer"/>, which needs <c>users:user:view</c>.</summary>
    public async Task<HttpStatusCode> ListUsersAsync(string bearer, WebApplicationFactory<Program>? host = null)
    {
        using var client = BearerClient(host, bearer);
        using var response = await client.GetAsync("/api/v1/admin/users");
        return response.StatusCode;
    }

    /// <summary>Ends the impersonation <paramref name="impersonationToken"/> carries: <c>DELETE /management/impersonate</c> with that token.</summary>
    public async Task<HttpStatusCode> EndImpersonationAsync(string impersonationToken, WebApplicationFactory<Program>? host = null)
    {
        using var client = BearerClient(host, impersonationToken);
        using var response = await client.DeleteAsync("/api/v1/management/impersonate");
        return response.StatusCode;
    }

    /// <summary>Revokes session <paramref name="sessionId"/> as the seeded platform admin: <c>POST /management/impersonation/sessions/{id}/revoke</c>.</summary>
    public async Task<HttpStatusCode> RevokeSessionAsync(string sessionId, WebApplicationFactory<Program>? host = null)
    {
        using var client = BearerClient(host, PlatformAdminToken());
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/management/impersonation/sessions/{sessionId}/revoke", new { reason = "test_revoke" });
        return response.StatusCode;
    }

    /// <summary>The ids of the sessions <c>GET /management/impersonation/sessions/active</c> lists, as the seeded platform admin.</summary>
    public async Task<IReadOnlyList<string>> ListActiveSessionIdsAsync(WebApplicationFactory<Program>? host = null)
    {
        using var client = BearerClient(host, PlatformAdminToken());
        using var response = await client.GetAsync("/api/v1/management/impersonation/sessions/active?pageSize=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "items", StringComparison.OrdinalIgnoreCase)).Value;
        return items.EnumerateArray().Select(item => ReadString(item, "id")).ToList();
    }

    /// <summary>
    /// Runs one pass of <paramref name="host"/>'s impersonation timeout sweep (this host when omitted)
    /// as if <paramref name="elapsed"/> had passed: the service Program.cs registers, built from the
    /// host's own services, with only its clock replaced.
    /// </summary>
    public Task SweepAsync(TimeSpan elapsed, WebApplicationFactory<Program>? host = null)
    {
        var clock = new FakeTimeProvider(TimeProvider.System.GetUtcNow() + elapsed);
        var sweep = ActivatorUtilities.CreateInstance<ImpersonationSessionTimeoutService>(
            (host ?? this).Services, clock);
        return sweep.SweepOnceAsync(CancellationToken.None);
    }

    /// <summary>The state <paramref name="host"/>'s session store (this host's when omitted) holds for <paramref name="sessionId"/>.</summary>
    public async Task<ImpersonationSessionStatus?> SessionStatusAsync(string sessionId, WebApplicationFactory<Program>? host = null)
    {
        var session = await (host ?? this).Services.GetRequiredService<IImpersonationSessionStore>()
            .GetAsync(sessionId, CancellationToken.None);
        return session?.Status;
    }

    /// <summary>Closes, in the store, every session in <paramref name="sessionIds"/> still open — a suite's cleanup.</summary>
    public async Task CloseSessionsAsync(IEnumerable<string> sessionIds, WebApplicationFactory<Program>? host = null)
    {
        var sessions = (host ?? this).Services.GetRequiredService<IImpersonationSessionStore>();
        foreach (var sessionId in sessionIds)
        {
            await sessions.RevokeAsync(
                sessionId, ImpersonationSessionStatus.ManuallyRevoked, "test_cleanup", DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
    }

    /// <summary>An ordinary access token for the seeded platform admin.</summary>
    public string PlatformAdminToken() => MintAccessToken(GetUser(PlatformAdminUserId, PlatformTenantId)!);

    /// <summary>
    /// An impersonation token for <paramref name="admin"/> into <paramref name="targetTenantId"/> that
    /// carries exactly <paramref name="permissions"/>, minted by this host's <see cref="JwtTokenService"/>
    /// the way <c>StartImpersonation</c> mints one — for a test that needs a permission set the stubbed
    /// role store does not give. No session is recorded for it.
    /// </summary>
    public string MintImpersonationToken(
        User admin, IEnumerable<string> permissions, bool readOnly, string targetTenantId = CustomerTenantId)
    {
        using var scope = Services.CreateScope();
        var (token, _, _) = scope.ServiceProvider.GetRequiredService<JwtTokenService>().GenerateImpersonationToken(
            admin,
            targetTenantId,
            new HashSet<string>(permissions, StringComparer.Ordinal),
            readOnly,
            impersonationSessionId: Guid.NewGuid().ToString("N"));
        return token;
    }

    /// <summary>
    /// A management API key of the platform tenant bound to <paramref name="owner"/>, stored in this host's
    /// <see cref="IApiKeyStore"/>. Returns the raw key, which authenticates as <c>Authorization: Bearer</c>.
    /// </summary>
    public string NewManagementKey(User owner)
    {
        var rawKey = $"test-management-key-{Guid.NewGuid():N}";
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(
            new ApiKey
            {
                KeyId = EntityId.From($"test-management-key-{Guid.NewGuid():N}"),
                TenantId = new TenantId(PlatformTenantId),
                Name = "Impersonation test key",
                HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
                Scopes = ["platform:tenant:impersonate"],
                UserId = owner.UserId,
                KeyType = ApiKeyType.Management,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None).GetAwaiter().GetResult();
        return rawKey;
    }

    private HttpClient BearerClient(WebApplicationFactory<Program>? host, string bearer)
    {
        var client = (host ?? this).CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private User CreateUser(User user, WebApplicationFactory<Program>[] replicas)
    {
        foreach (var host in Hosts(replicas))
            CreateUserIn(host.Services, user);

        return user;
    }

    private static void CreateUserIn(IServiceProvider services, User user)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUserStore>().CreateAsync(user, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private IEnumerable<WebApplicationFactory<Program>> Hosts(WebApplicationFactory<Program>[] replicas) =>
        new WebApplicationFactory<Program>[] { this }.Concat(replicas);

    private static User NewUser(string userId, string tenantId) => new()
    {
        UserId = EntityId.From(userId),
        TenantId = new TenantId(tenantId),
        Email = $"{userId}@impersonation.test",
        DisplayName = userId,
        Role = UserRole.Admin,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Tenant NewTenant(string tenantId, TenantType type, string parentTenantId) => new()
    {
        TenantId = tenantId,
        Name = tenantId,
        Status = TenantStatus.Active,
        Type = type,
        ParentTenantId = parentTenantId,
    };

    private static string ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.GetString()!;
        }

        throw new InvalidOperationException($"The response carries no '{name}' field.");
    }
}

/// <summary>A started impersonation: the token it issued and the id of its session.</summary>
public sealed record StartedImpersonation(string AccessToken, string SessionId);

/// <summary>A Partner tenant's admin and the Customer child it can impersonate.</summary>
public sealed record PartnerActor(User Admin, string CustomerTenantId);
