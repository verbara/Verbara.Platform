using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class AuthAdminTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public AuthAdminTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
        _anonClient = factory.CreateClient();
    }

    [Fact]
    public async Task GetConfig_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("mfaPolicy");
    }

    [Fact]
    public async Task UpdateConfig_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            passwordMinLength = 16,
            lockoutThreshold = 3
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("16");
    }

    [Fact]
    public async Task ListEvents_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListSessions_ShouldReturn200_WhenNoUserId()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListSessions_ShouldReturn200_WhenUserIdProvided()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/sessions?userId=test-user");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokeSession_ShouldReturn204_WhenAuthenticated()
    {
        var response = await _authClient.DeleteAsync("/api/admin/auth/sessions/some-token-id");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetConfig_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _anonClient.GetAsync("/api/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

public sealed class AuthAdminSessionListingTests : IClassFixture<AuthAdminSessionListingTests.SeededSessionsFactory>
{
    private const string TenantId = AuthenticatedPlatformApiFactory.TestTenantId;
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private readonly HttpClient _client;

    public AuthAdminSessionListingTests(SeededSessionsFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    private sealed record SessionDto(
        string SessionId,
        string UserId,
        string UserEmail,
        string UserDisplayName,
        string? IpAddress,
        string? UserAgent,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastActivity);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ListSessions_ShouldReturnAllTenantSessionsWithUserInfo_WhenNoUserIdProvided()
    {
        var response = await _client.GetAsync("/api/admin/auth/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var sessions = JsonSerializer.Deserialize<List<SessionDto>>(body, JsonOpts);

        sessions.Should().NotBeNull();
        sessions!.Should().HaveCount(3);

        sessions!.Where(s => s.UserId == UserA).Should().HaveCount(2)
            .And.AllSatisfy(s =>
            {
                s.UserEmail.Should().Be("user-a@test.internal");
                s.UserDisplayName.Should().Be("User A");
            });

        var sessionB = sessions!.Single(s => s.UserId == UserB);
        sessionB.UserEmail.Should().Be("user-b@test.internal");
        sessionB.UserDisplayName.Should().Be("User B");

        sessions.Should().AllSatisfy(s =>
        {
            s.SessionId.Should().NotBeNullOrWhiteSpace();
            s.LastActivity.Should().NotBe(default);
        });
    }

    [Fact]
    public async Task ListSessions_ShouldSkipSessions_WhenUserWasDeleted()
    {
        // The seeded tokens include one orphan (userId=ghost-user, no matching User).
        // All three regular sessions still return, but nothing is returned for the ghost.
        var response = await _client.GetAsync("/api/admin/auth/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var sessions = JsonSerializer.Deserialize<List<SessionDto>>(body, JsonOpts);

        sessions.Should().NotBeNull();
        sessions!.Select(s => s.UserId).Should().NotContain("ghost-user");
        sessions.Should().HaveCount(3, because: "orphan session for deleted user must be skipped, not surfaced or errored");
    }

    [Fact]
    public async Task ListSessions_ShouldFilterByUser_WhenUserIdProvided()
    {
        var response = await _client.GetAsync($"/api/admin/auth/sessions?userId={UserA}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var sessions = JsonSerializer.Deserialize<List<SessionDto>>(body, JsonOpts);

        sessions.Should().NotBeNull();
        sessions!.Should().HaveCount(2);
        sessions.Should().AllSatisfy(s =>
        {
            s.UserId.Should().Be(UserA);
            s.UserEmail.Should().Be("user-a@test.internal");
        });
    }

    /// <summary>
    /// Factory that pre-seeds two users + three active refresh tokens + one orphan token
    /// (for a user that was deleted) so the tenant-wide session listing path can be
    /// exercised end-to-end.
    /// </summary>
    public sealed class SeededSessionsFactory : WebApplicationFactory<Program>
    {
        private const string TestApiKey = AuthenticatedPlatformApiFactory.TestApiKey;

        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
            client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
            return client;
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var hashedKey = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TestApiKey)));

                AuthenticatedPlatformApiFactory.SetupTestAuth(services, hashedKey, TenantId, "test-admin-user");
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
            });

            builder.ConfigureServices(services =>
            {
                // Replace IUserStore substitute from the base factory with one that
                // also answers GetByIdsAsync for user-a + user-b (but NOT ghost-user).
                // AHH Phase 1: filter !IsKeyedService (keyed-as-inner preserved).
                var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService);
                if (userStoreDescriptor is not null) services.Remove(userStoreDescriptor);

                var userStore = Substitute.For<IUserStore>();
                var tenant = new TenantId(TenantId);

                var userA = new User
                {
                    UserId = EntityId.From(UserA),
                    TenantId = tenant,
                    Email = "user-a@test.internal",
                    DisplayName = "User A",
                    Role = UserRole.Agent,
                    Status = UserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                var userB = new User
                {
                    UserId = EntityId.From(UserB),
                    TenantId = tenant,
                    Email = "user-b@test.internal",
                    DisplayName = "User B",
                    Role = UserRole.Supervisor,
                    Status = UserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                };

                // Admin user needed by AdminOnly policy / API-key auth flow.
                var admin = new User
                {
                    UserId = EntityId.From("test-admin-user"),
                    TenantId = tenant,
                    Email = "test-admin@test.internal",
                    DisplayName = "Test Admin",
                    Role = UserRole.Admin,
                    Status = UserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                };

                userStore.GetByIdAsync(tenant, EntityId.From("test-admin-user"), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<User?>(admin));
                userStore.GetByIdAsync(tenant, EntityId.From(UserA), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<User?>(userA));
                userStore.GetByIdAsync(tenant, EntityId.From(UserB), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<User?>(userB));

                userStore.GetByIdsAsync(TenantId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                    .Returns(ci =>
                    {
                        var ids = ci.ArgAt<IReadOnlyCollection<string>>(1);
                        var found = new List<User>();
                        foreach (var id in ids)
                        {
                            if (id == UserA) found.Add(userA);
                            else if (id == UserB) found.Add(userB);
                            // ghost-user intentionally NOT added — simulates deleted user
                        }
                        return Task.FromResult<IReadOnlyList<User>>(found);
                    });

                userStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
                    .Returns(ci => Task.FromResult(PagedResult<User>.Empty(
                        ci.ArgAt<PagedQuery>(1).Page,
                        ci.ArgAt<PagedQuery>(1).PageSize)));

                services.AddSingleton(userStore);

                // Replace IRefreshTokenStore with a pre-seeded in-memory store.
                var rtDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IRefreshTokenStore));
                if (rtDescriptor is not null) services.Remove(rtDescriptor);

                var tokenStore = new InMemoryRefreshTokenStore();
                var now = DateTimeOffset.UtcNow;
                var expires = now.AddDays(7);

                SeedToken(tokenStore, "tok-a-1", UserA, TenantId, now, expires);
                SeedToken(tokenStore, "tok-a-2", UserA, TenantId, now, expires);
                SeedToken(tokenStore, "tok-b-1", UserB, TenantId, now, expires);
                // Orphan: active token pointing at a user that no longer exists.
                SeedToken(tokenStore, "tok-ghost", "ghost-user", TenantId, now, expires);

                services.AddSingleton<IRefreshTokenStore>(tokenStore);
            });

            var host = base.CreateHost(builder);
            AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TenantId);
            return host;
        }

        private static void SeedToken(InMemoryRefreshTokenStore store, string tokenId, string userId, string tenantId, DateTimeOffset created, DateTimeOffset expires)
        {
            store.SaveAsync(new RefreshToken
            {
                TokenId = tokenId,
                UserId = userId,
                TenantId = tenantId,
                TokenHash = tokenId + "-hash",
                CreatedAt = created,
                ExpiresAt = expires,
                LastActivityAt = created,
                IpAddress = "127.0.0.1",
                UserAgent = "xunit/1.0",
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
