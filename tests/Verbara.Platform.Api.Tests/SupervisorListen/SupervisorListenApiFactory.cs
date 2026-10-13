using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen: a host whose callers are two Supervisor-role users of tenant <c>acme</c> — one that holds
/// <c>contacts:conversation:monitor</c> and one that does not — and no telephony, so the listen service is absent
/// (<c>voice-unavailable</c>). <see cref="SupervisorListenVoiceApiFactory"/> adds a listen service wired to fakes.
/// </summary>
public class SupervisorListenApiFactory : WebApplicationFactory<Program>
{
    public const string Monitor = "contacts:conversation:monitor";
    public const string PermittedKey = "listen-permitted-key-11111";
    public const string UnpermittedKey = "listen-unpermitted-key-22222";
    public const string UnpermittedUserId = "8e3d0f5c2b6a4d1f9c7e1a2b3c4d5e6f";

    internal ListenHarness Harness { get; } = new();

    /// <summary>The realtime endpoint-profile store of the host (in memory).</summary>
    public FakeEndpointProfiles EndpointProfiles { get; } = new();

    /// <summary>The realtime sync service of the host (a substitute).</summary>
    public IRealtimeSyncService Sync { get; } = Substitute.For<IRealtimeSyncService>();

    /// <summary>The permitted supervisor's user id (also the harness's supervisor).</summary>
    public static string PermittedUserId => ListenHarness.Supervisor.Value;

    protected virtual bool WithListenService => false;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            SetupSupervisors(services);
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);
            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            services.RemoveAll<EndpointProfileStoreBase>();
            services.AddSingleton<EndpointProfileStoreBase>(EndpointProfiles);
            services.RemoveAll<IRealtimeSyncService>();
            services.AddSingleton(Sync);

            if (WithListenService)
            {
                services.RemoveAll<SupervisorListenService>();
                services.AddSingleton(sp =>
                {
                    Harness.Profiles = sp.GetRequiredService<ISupervisorListenProfileStore>();
                    return Harness.Service();
                });
            }
        });

        var host = base.CreateHost(builder);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, ListenHarness.Tenant);
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, ListenHarness.Tenant);
        return host;
    }

    public HttpClient CreateSupervisorClient(bool permitted = true)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {(permitted ? PermittedKey : UnpermittedKey)}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", ListenHarness.Tenant);
        return client;
    }

    private static void SetupSupervisors(IServiceCollection services)
    {
        var tenant = ListenHarness.TenantId;
        var users = new Dictionary<string, (string Key, HashSet<string> Permissions)>(StringComparer.Ordinal)
        {
            [PermittedUserId] = (PermittedKey, new HashSet<string>(StringComparer.Ordinal) { Monitor }),
            [UnpermittedUserId] = (UnpermittedKey, new HashSet<string>(StringComparer.Ordinal) { "contacts:conversation:read" }),
        };

        services.RemoveAll<IApiKeyStore>();
        var keys = Substitute.For<IApiKeyStore>();
        foreach (var (userId, (key, _)) in users)
        {
            keys.GetByHashAsync(Hash(key), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ApiKey?>(new ApiKey
            {
                KeyId = EntityId.From($"key-{userId}"),
                TenantId = tenant,
                Name = "Supervisor key",
                HashedKey = Hash(key),
                Scopes = ["*"],
                UserId = EntityId.From(userId),
                IsRevoked = false,
                CreatedAt = DateTimeOffset.UtcNow,
            }));
        }

        services.AddSingleton(keys);

        var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService);
        if (userStoreDescriptor is not null)
            services.Remove(userStoreDescriptor);
        var userStore = Substitute.For<IUserStore>();
        userStore.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(users.ContainsKey(ci.ArgAt<EntityId>(1).Value) && ci.ArgAt<TenantId>(0) == tenant
                ? new User
                {
                    UserId = ci.ArgAt<EntityId>(1), TenantId = tenant, Email = $"{ci.ArgAt<EntityId>(1).Value}@acme.test",
                    DisplayName = "Ana Supervisor", Role = UserRole.Supervisor, Status = UserStatus.Active, CreatedAt = DateTimeOffset.UtcNow,
                }
                : null));
        userStore.GetByIdsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<User>>([]));
        services.AddSingleton(userStore);

        services.RemoveAll<IUserRoleStore>();
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlySet<string>>(
                users.TryGetValue(ci.ArgAt<EntityId>(1).Value, out var u) ? u.Permissions : new HashSet<string>()));
        services.AddSingleton(roles);
    }

    private static string Hash(string rawKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));
}

/// <summary>A <see cref="SupervisorListenApiFactory"/> whose host has a listen service wired to the harness's fakes.</summary>
public sealed class SupervisorListenVoiceApiFactory : SupervisorListenApiFactory
{
    protected override bool WithListenService => true;
}

/// <summary>An in-memory realtime endpoint-profile store.</summary>
public sealed class FakeEndpointProfiles : EndpointProfileStoreBase
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, EndpointProfile> _store = new();
    private long _nextId;

    public int Created => _store.Count;

    public override ValueTask<EndpointProfile?> GetAsync(long id, string tenantId, CancellationToken ct = default) =>
        ValueTask.FromResult(_store.TryGetValue(id, out var p) && p.TenantId == tenantId ? p : null);

    public override ValueTask<EndpointProfile?> GetDefaultAsync(string tenantId, EndpointProfileType type, CancellationToken ct = default) =>
        ValueTask.FromResult<EndpointProfile?>(new EndpointProfile { TenantId = tenantId, Name = "WebRTC Agent", IsDefault = true, Webrtc = true });

    public override ValueTask<IReadOnlyList<EndpointProfile>> ListAsync(string tenantId, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<EndpointProfile>>([.. _store.Values.Where(p => p.TenantId == tenantId).OrderBy(p => p.Id)]);

    public override ValueTask<long> CreateAsync(EndpointProfile profile, string tenantId, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        profile.Id = id;
        profile.TenantId = tenantId;
        _store[id] = profile;
        return ValueTask.FromResult(id);
    }

    public override ValueTask UpdateAsync(EndpointProfile profile, string tenantId, CancellationToken ct = default)
    {
        _store[profile.Id] = profile;
        return ValueTask.CompletedTask;
    }

    public override ValueTask DeleteAsync(long id, string tenantId, CancellationToken ct = default)
    {
        _store.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }

    public override ValueTask SeedDefaultsAsync(string tenantId, CancellationToken ct = default) => ValueTask.CompletedTask;
}
