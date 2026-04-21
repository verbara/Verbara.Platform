using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.Licensing;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// P0 regression suite for the impersonation privilege-escalation + audit-evasion
/// vulnerabilities fixed in v1.9.0 Platform.
///
/// Gap 1: Before the fix, any tenant admin holding <c>platform:tenant:impersonate</c>
/// could impersonate into an unrelated peer tenant because the endpoint only verified
/// target-tenant existence / active status, never that the target was in the caller's
/// hierarchy. This suite locks in the hierarchy check.
///
/// Gap 2: Before the fix, only the caller tenant saw an audit entry ("impersonation
/// started"). The target tenant — the party actually being accessed — had no record.
/// This suite pins dual-audit emission (caller + target).
///
/// Test hierarchy:
/// <code>
/// platform (Platform)
///  ├── partner-alpha (Partner)
///  │    └── customer-alpha-child (Customer)
///  ├── customer-bravo (Customer, sibling of partner-alpha)
///  └── customer-charlie (Customer, sibling — target for escalation attempts)
/// </code>
/// </summary>
public sealed class ImpersonationPrivilegeEscalationTests
    : IClassFixture<ImpersonationPrivilegeEscalationTests.HierarchyImpersonationFactory>
{
    private readonly HierarchyImpersonationFactory _factory;

    public ImpersonationPrivilegeEscalationTests(HierarchyImpersonationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NonPlatformAdmin_ShouldReceive403_WhenImpersonatingUnrelatedTenant()
    {
        // partner-alpha admin tries to impersonate customer-bravo (sibling, NOT descendant).
        var client = _factory.CreateClientFor(HierarchyImpersonationFactory.PartnerAlphaKey);
        var before = _factory.AuthEventCountByTenant(HierarchyImpersonationFactory.PartnerAlphaTenantId);

        var response = await client.PostAsJsonAsync(
            "/api/management/impersonate",
            new ImpersonateRequestDto(HierarchyImpersonationFactory.CustomerBravoTenantId, ReadOnly: false),
            TestJson.Options);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "partner-alpha must not be able to impersonate an unrelated peer tenant");

        // A privilege-escalation audit entry must land in the caller's tenant log
        // (the *caller* is the principal whose history should show this attempt;
        //  the target must NOT see a target.accessed entry because access was denied).
        var callerEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.PartnerAlphaTenantId);
        callerEntries.Should().Contain(
            e => e.EventType == AuthEventTypes.ImpersonationPrivilegeEscalationAttempted,
            because: "rejected attempts must be auditable in the caller tenant");

        var targetEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.CustomerBravoTenantId);
        targetEntries.Should().NotContain(
            e => e.EventType == AuthEventTypes.ImpersonationTargetAccessed,
            because: "no access was granted; target must not see a target.accessed entry");

        // Delta check: exactly one NEW attempt event was added to caller's tenant log.
        var after = _factory.AuthEventCountByTenant(HierarchyImpersonationFactory.PartnerAlphaTenantId);
        (after - before).Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task NonPlatformAdmin_ShouldSucceed_WhenImpersonatingOwnDescendantTenant()
    {
        // partner-alpha admin impersonates customer-alpha-child (direct descendant).
        var client = _factory.CreateClientFor(HierarchyImpersonationFactory.PartnerAlphaKey);

        var response = await client.PostAsJsonAsync(
            "/api/management/impersonate",
            new ImpersonateRequestDto(HierarchyImpersonationFactory.CustomerAlphaChildTenantId, ReadOnly: false),
            TestJson.Options);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "parent admins CAN impersonate into their own descendant tenants");

        var payload = await response.Content.ReadFromJsonAsync<ImpersonateResponseDto>(TestJson.Options);
        payload.Should().NotBeNull();
        payload!.AccessToken.Should().NotBeNullOrEmpty();
        payload.TargetTenantId.Should().Be(HierarchyImpersonationFactory.CustomerAlphaChildTenantId);

        // DUAL audit check — caller + target.
        var callerEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.PartnerAlphaTenantId);
        callerEntries.Should().Contain(
            e => e.EventType == AuthEventTypes.ImpersonationStarted,
            because: "caller tenant log must record the impersonation.started event");

        var targetEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.CustomerAlphaChildTenantId);
        targetEntries.Should().Contain(
            e => e.EventType == AuthEventTypes.ImpersonationTargetAccessed,
            because: "target tenant admins MUST see who accessed their tenant");
    }

    [Fact]
    public async Task PlatformAdmin_ShouldSucceed_WhenImpersonatingAnyCustomerTenant()
    {
        // platform host admin impersonates customer-charlie (completely unrelated to anyone).
        var client = _factory.CreateClientFor(HierarchyImpersonationFactory.PlatformAdminKey);

        var response = await client.PostAsJsonAsync(
            "/api/management/impersonate",
            new ImpersonateRequestDto(HierarchyImpersonationFactory.CustomerCharlieTenantId, ReadOnly: true),
            TestJson.Options);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "platform admins (host tenant OR platform:* permission holders) bypass hierarchy check by design");

        var payload = await response.Content.ReadFromJsonAsync<ImpersonateResponseDto>(TestJson.Options);
        payload.Should().NotBeNull();
        payload!.ReadOnly.Should().BeTrue();

        // DUAL audit — caller (platform) + target (customer-charlie).
        var callerEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.PlatformTenantId);
        callerEntries.Should().Contain(e => e.EventType == AuthEventTypes.ImpersonationStarted);

        var targetEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.CustomerCharlieTenantId);
        targetEntries.Should().Contain(e => e.EventType == AuthEventTypes.ImpersonationTargetAccessed);
    }

    [Fact]
    public async Task AuditEntry_ShouldBeLoggedToTargetTenant_OnSuccessfulImpersonation()
    {
        // Focused regression for the audit-evasion half of the P0 (Gap 2). Uses
        // platform admin → customer-bravo so there's no escalation-check noise.
        var client = _factory.CreateClientFor(HierarchyImpersonationFactory.PlatformAdminKey);
        var targetBefore = _factory.AuthEventCountByTenant(HierarchyImpersonationFactory.CustomerBravoTenantId);

        var response = await client.PostAsJsonAsync(
            "/api/management/impersonate",
            new ImpersonateRequestDto(HierarchyImpersonationFactory.CustomerBravoTenantId, ReadOnly: false),
            TestJson.Options);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var targetEntries = _factory.GetAuthEvents(HierarchyImpersonationFactory.CustomerBravoTenantId);
        var accessed = targetEntries.SingleOrDefault(
            e => e.EventType == AuthEventTypes.ImpersonationTargetAccessed);

        accessed.Should().NotBeNull(
            because: "target tenants MUST receive an impersonation.target.accessed audit entry — this is the audit-evasion regression pin");
        accessed!.TenantId.Should().Be(HierarchyImpersonationFactory.CustomerBravoTenantId);
        accessed.UserId.Should().Be(HierarchyImpersonationFactory.PlatformAdminUserId);

        var targetAfter = _factory.AuthEventCountByTenant(HierarchyImpersonationFactory.CustomerBravoTenantId);
        (targetAfter - targetBefore).Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task NonPlatformAdmin_ShouldSucceed_WhenImpersonatingSelfTenant()
    {
        // Edge case: admin tries to impersonate their own tenant. Documented behavior:
        // self-impersonation is permitted (the hierarchy check short-circuits on
        // self-equality). It's a degenerate case but NOT a privilege escalation,
        // so we pin the current permissive behavior to prevent accidental regressions.
        var client = _factory.CreateClientFor(HierarchyImpersonationFactory.PartnerAlphaKey);

        var response = await client.PostAsJsonAsync(
            "/api/management/impersonate",
            new ImpersonateRequestDto(HierarchyImpersonationFactory.PartnerAlphaTenantId, ReadOnly: true),
            TestJson.Options);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "self-impersonation is permitted (no cross-tenant privilege uplift happens)");
    }

    // ─── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Custom factory that pre-seeds a realistic tenant hierarchy, multiple admin
    /// users with distinct API keys, and a stubbed <see cref="IUserRoleStore"/>
    /// that grants <c>platform:tenant:impersonate</c> to non-platform admins so
    /// the endpoint's pre-hierarchy permission check passes and we actually exercise
    /// the new hierarchy gate.
    /// </summary>
    public sealed class HierarchyImpersonationFactory : WebApplicationFactory<Program>
    {
        public const string PlatformTenantId = "platform";
        public const string PartnerAlphaTenantId = "partner-alpha";
        public const string CustomerAlphaChildTenantId = "customer-alpha-child";
        public const string CustomerBravoTenantId = "customer-bravo";
        public const string CustomerCharlieTenantId = "customer-charlie";

        public const string PlatformAdminUserId = "platform-admin-user";
        public const string PartnerAlphaUserId = "partner-alpha-admin-user";

        public const string PlatformAdminKey = "mgmt-platform-key";
        public const string PartnerAlphaKey = "mgmt-partner-alpha-key";

        private readonly IAuthEventStore _sharedAuthEventStore = new CapturingAuthEventStore();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubAsteriskHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                services.Configure<LicenseOptions>(o => o.EnforcementMode = EnforcementMode.Disabled);
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                // Replace the IAuthEventStore with a capturing in-memory impl so we can
                // assert dual-audit emission (caller + target) per tenant.
                var authStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAuthEventStore));
                if (authStoreDescriptor is not null) services.Remove(authStoreDescriptor);
                services.AddSingleton(_sharedAuthEventStore);

                // Stub IUserRoleStore so non-platform admins hold platform:tenant:impersonate.
                // Required for the endpoint's pre-hierarchy permission check to pass; otherwise
                // we'd 403 early on PermissionResolver and never reach the security check under test.
                var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
                if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);

                var roleStore = Substitute.For<IUserRoleStore>();
                var impersonatePerms = new HashSet<string>(StringComparer.Ordinal)
                {
                    "platform:tenant:impersonate",
                    "contacts:contact:view",
                    "queues:queue:view",
                };
                roleStore.GetEffectivePermissionsAsync(
                        Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlySet<string>>(impersonatePerms));
                services.AddSingleton(roleStore);
            });

            var host = base.CreateHost(builder);
            SeedHierarchyAndKeys(host.Services);
            return host;
        }

        private static void SeedHierarchyAndKeys(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
            var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
            var apiKeyStore = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();

            // Tenant hierarchy
            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = PlatformTenantId,
                Name = "Platform Host",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
            }).AsTask().GetAwaiter().GetResult();

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = PartnerAlphaTenantId,
                Name = "Partner Alpha",
                Status = TenantStatus.Active,
                Type = TenantType.Partner,
                ParentTenantId = PlatformTenantId,
            }).AsTask().GetAwaiter().GetResult();

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = CustomerAlphaChildTenantId,
                Name = "Customer Alpha Child",
                Status = TenantStatus.Active,
                Type = TenantType.Customer,
                ParentTenantId = PartnerAlphaTenantId,
            }).AsTask().GetAwaiter().GetResult();

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = CustomerBravoTenantId,
                Name = "Customer Bravo",
                Status = TenantStatus.Active,
                Type = TenantType.Customer,
                ParentTenantId = PlatformTenantId, // sibling of partner-alpha
            }).AsTask().GetAwaiter().GetResult();

            tenantStore.UpsertAsync(new Tenant
            {
                TenantId = CustomerCharlieTenantId,
                Name = "Customer Charlie",
                Status = TenantStatus.Active,
                Type = TenantType.Customer,
                ParentTenantId = PlatformTenantId,
            }).AsTask().GetAwaiter().GetResult();

            // Admin users — one per tenant-of-interest.
            userStore.SaveAsync(new User
            {
                UserId = EntityId.From(PlatformAdminUserId),
                TenantId = new TenantId(PlatformTenantId),
                Email = "platform-admin@test.internal",
                DisplayName = "Platform Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();

            userStore.SaveAsync(new User
            {
                UserId = EntityId.From(PartnerAlphaUserId),
                TenantId = new TenantId(PartnerAlphaTenantId),
                Email = "partner-alpha-admin@test.internal",
                DisplayName = "Partner Alpha Admin",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();

            // Management API keys bound to each admin user. KeyId == user id so the
            // endpoint's `NameIdentifier` claim (populated from KeyId.Value by
            // ApiKeyAuthenticationHandler) resolves to the correct user id for the
            // per-tenant permission lookup. This mirrors what a real management key
            // tied to a user looks like in production.
            apiKeyStore.SaveAsync(new ApiKey
            {
                KeyId = EntityId.From(PlatformAdminUserId),
                TenantId = new TenantId(PlatformTenantId),
                Name = "Platform Admin Key",
                HashedKey = HashKey(PlatformAdminKey),
                Scopes = ["platform:*"],
                UserId = EntityId.From(PlatformAdminUserId),
                KeyType = ApiKeyType.Management,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();

            apiKeyStore.SaveAsync(new ApiKey
            {
                KeyId = EntityId.From(PartnerAlphaUserId),
                TenantId = new TenantId(PartnerAlphaTenantId),
                Name = "Partner Alpha Admin Key",
                HashedKey = HashKey(PartnerAlphaKey),
                Scopes = ["*"], // note: NOT platform:* — this user is NOT a platform admin
                UserId = EntityId.From(PartnerAlphaUserId),
                KeyType = ApiKeyType.Management,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None).GetAwaiter().GetResult();
        }

        public HttpClient CreateClientFor(string bearerToken)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearerToken}");
            return client;
        }

        public IReadOnlyList<AuthEvent> GetAuthEvents(string tenantId)
            => ((CapturingAuthEventStore)_sharedAuthEventStore).Snapshot(tenantId);

        public int AuthEventCountByTenant(string tenantId)
            => GetAuthEvents(tenantId).Count;

        private static string HashKey(string rawKey)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexStringLower(bytes);
        }
    }

    /// <summary>
    /// Minimal IAuthEventStore that captures everything in memory so tests can
    /// assert per-tenant audit emission without a database.
    /// </summary>
    internal sealed class CapturingAuthEventStore : IAuthEventStore
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<AuthEvent> _events = [];

        public Task SaveAsync(AuthEvent authEvent, CancellationToken ct)
        {
            _events.Add(authEvent);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuthEvent> Snapshot(string tenantId)
            => _events.Where(e => e.TenantId == tenantId)
                .OrderByDescending(e => e.CreatedAt)
                .ToList();

        public Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct)
        {
            var items = _events.Where(e => e.TenantId == tenantId).ToList();
            return Task.FromResult(new PagedResult<AuthEvent>(items, items.Count, page, pageSize));
        }

        public Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct)
        {
            var items = _events.Where(e => e.TenantId == tenantId && e.UserId == userId).ToList();
            return Task.FromResult(new PagedResult<AuthEvent>(items, items.Count, page, pageSize));
        }

        public Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct)
        {
            var items = _events.Where(e => e.TenantId == tenantId).ToList();
            return Task.FromResult(new PagedResult<AuthEvent>(items, items.Count, query.Page, query.PageSize));
        }

        public Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
        {
            IReadOnlyList<AuthEvent> items = _events
                .Where(e => e.TenantId == tenantId && e.UserId == userId)
                .ToList();
            return Task.FromResult(items);
        }

        public Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0);
    }

    // ─── DTOs (test-local mirror — production DTOs are internal) ──────────────

    private sealed record ImpersonateRequestDto(string TargetTenantId, bool ReadOnly = false);
    private sealed record ImpersonateResponseDto(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        string TargetTenantId,
        string TargetTenantName,
        bool ReadOnly);

    private static class TestJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
