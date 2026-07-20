using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Decorators;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// ADR-0012 Ola-3 — DI wire-up for the best-effort Asterisk-realtime-sync store decorators.
/// Migrates the queue / agent / queue-member sync out of the endpoint Service-Locator resolves
/// (<c>context.RequestServices.GetService&lt;IRealtimeSyncService&gt;()</c>) and into the storage
/// seam, exactly as <c>AddAuthHotpathCaching</c> does for the auth cache decorators:
/// register the concrete store keyed-as-inner, remove the unkeyed alias, then re-register the
/// unkeyed service as a decorator that resolves its keyed inner.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Call AFTER <c>AddPostgresStorage</c> / <c>AddInMemoryStorage</c> (they register the
/// unkeyed <see cref="IQueueStore"/> / <see cref="IAgentStore"/> / <see cref="IQueueMembershipStore"/>
/// this method re-keys). Running it before storage would key nothing.
/// </para>
/// <para>
/// <b>Pass-through when realtime is absent.</b> Each decorator is registered via a factory that
/// resolves <see cref="IRealtimeSyncService"/> at first-use; when it is not registered (the SMB
/// no-dialer path, lab tear-downs), the factory returns the UNDECORATED keyed inner, so the store
/// behaves exactly as before. No behavioral change when realtime is off.
/// </para>
/// </remarks>
public static class RealtimeSyncingStoresExtensions
{
    internal const string QueueStoreInner = "realtime.syncing.queue-store-inner";
    internal const string AgentStoreInner = "realtime.syncing.agent-store-inner";
    internal const string QueueMembershipStoreInner = "realtime.syncing.queue-membership-store-inner";

    /// <summary>
    /// Wires the ADR-0012 Ola-3 realtime-sync decorators on top of the previously registered
    /// <see cref="IQueueStore"/> + <see cref="IAgentStore"/> + <see cref="IQueueMembershipStore"/>
    /// implementations. Call AFTER <c>AddPostgresStorage</c> / <c>AddInMemoryStorage</c>.
    /// </summary>
    public static IServiceCollection AddRealtimeSyncingStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Re-key each concrete store as the inner, drop the unkeyed alias, then re-register
        // the unkeyed service as the decorator (which falls back to the inner when realtime
        // is not registered).
        RekeyInnerFromUnkeyed<IQueueStore>(services, QueueStoreInner);
        services.AddSingleton<IQueueStore>(sp =>
        {
            var inner = sp.GetRequiredKeyedService<IQueueStore>(QueueStoreInner);
            var sync = sp.GetService<IRealtimeSyncService>();
            return sync is null
                ? inner
                : new RealtimeSyncingQueueStore(
                    inner, sync, sp.GetRequiredService<ILogger<RealtimeSyncingQueueStore>>());
        });

        RekeyInnerFromUnkeyed<IAgentStore>(services, AgentStoreInner);
        services.AddSingleton<IAgentStore>(sp =>
        {
            var inner = sp.GetRequiredKeyedService<IAgentStore>(AgentStoreInner);
            var sync = sp.GetService<IRealtimeSyncService>();
            return sync is null
                ? inner
                : new RealtimeSyncingAgentStore(
                    inner, sync, sp.GetRequiredService<ILogger<RealtimeSyncingAgentStore>>());
        });

        RekeyInnerFromUnkeyed<IQueueMembershipStore>(services, QueueMembershipStoreInner);
        services.AddSingleton<IQueueMembershipStore>(sp =>
        {
            var inner = sp.GetRequiredKeyedService<IQueueMembershipStore>(QueueMembershipStoreInner);
            var sync = sp.GetService<IRealtimeSyncService>();
            return sync is null
                ? inner
                // R3: resolve queue/agent name lookups via the KEYED (undecorated) inners so a
                // membership write never re-enters the queue/agent decorator's own sync path.
                : new RealtimeSyncingQueueMembershipStore(
                    inner,
                    sp.GetRequiredKeyedService<IQueueStore>(QueueStoreInner),
                    sp.GetRequiredKeyedService<IAgentStore>(AgentStoreInner),
                    sync,
                    sp.GetRequiredService<ILogger<RealtimeSyncingQueueMembershipStore>>());
        });

        return services;
    }

    /// <summary>
    /// Registers the <see cref="TrunkStoreBase"/> implementation: the realtime-syncing Postgres
    /// decorator when a dialer connection string is present, otherwise the no-op in-memory store.
    /// Relocated verbatim out of the Api composition root (ADR-0012 Ola-3 gate #9 LOC budget).
    /// </summary>
    public static IServiceCollection AddRealtimeSyncingTrunkStore(
        this IServiceCollection services, string? dialerConnectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Trunk decorator — wraps PostgresTrunkStore with Realtime sync (only when Dialer is configured)
        if (!string.IsNullOrEmpty(dialerConnectionString))
        {
            services.AddSingleton<TrunkStoreBase>(sp =>
                new RealtimeSyncingTrunkStore(
                    new PostgresTrunkStore(sp.GetRequiredService<DialerDbContext>()),
                    sp.GetRequiredService<IRealtimeSyncService>()));
        }
        else
        {
            services.AddSingleton<TrunkStoreBase>(_ => new InMemoryTrunkStore());
        }

        return services;
    }

    /// <summary>
    /// Re-registers the concrete implementation currently behind the UNKEYED <typeparamref name="TService"/>
    /// registration as a keyed inner, then removes the unkeyed alias so the decorator can claim it.
    /// </summary>
    /// <remarks>
    /// Both <c>AddPostgresStorage</c> and <c>AddInMemoryStorage</c> register the three target stores as
    /// <c>AddSingleton&lt;IFoo, ConcreteFoo&gt;()</c> — an <see cref="ServiceDescriptor.ImplementationType"/>
    /// registration. That single shape is all this method handles; a factory/instance registration (or a
    /// missing one) means the composition order was broken, so we fail loudly rather than silently skip.
    /// </remarks>
    private static void RekeyInnerFromUnkeyed<TService>(IServiceCollection services, string innerKey)
        where TService : class
    {
        ServiceDescriptor? unkeyed = null;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType == typeof(TService) && !d.IsKeyedService)
            {
                unkeyed = d;
                services.RemoveAt(i);
            }
        }

        if (unkeyed?.ImplementationType is null)
            throw new InvalidOperationException(
                $"AddRealtimeSyncingStores requires an unkeyed {typeof(TService).Name} registration " +
                "backed by a concrete implementation type (call AddPostgresStorage / AddInMemoryStorage first).");

        services.Add(new ServiceDescriptor(
            typeof(TService), innerKey, unkeyed.ImplementationType, ServiceLifetime.Singleton));
    }
}

/// <summary>No-op trunk store used when Dialer is not configured (relocated from Program.cs).</summary>
internal sealed class InMemoryTrunkStore : TrunkStoreBase
{
    public override ValueTask<IReadOnlyList<Trunk>> ListAsync(string tenantId, CancellationToken ct = default)
        => new(Array.Empty<Trunk>());

    public override ValueTask<IReadOnlyList<Trunk>> ListActiveAsync(string tenantId, CancellationToken ct = default)
        => new(Array.Empty<Trunk>());
}
