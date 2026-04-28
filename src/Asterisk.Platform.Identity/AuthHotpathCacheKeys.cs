namespace Asterisk.Platform.Identity;

/// <summary>
/// Constant keys used by the Auth Hotpath Hardening (AHH) Phase 1 cache
/// decorator pipeline. Concrete store registrations are keyed under these
/// names; the unkeyed <see cref="IUserStore"/> / <see cref="ITenantAuthConfigStore"/>
/// public registrations point at the cache decorators in the Api project.
/// </summary>
/// <remarks>
/// Plan: docs/plans/active/2026-04-27-auth-hotpath-hardening.md Phase 1.
/// Lives in <c>Asterisk.Platform.Identity</c> (not Api or Storage.Postgres) so
/// the keyed-registration coordination stays AOT-safe and visible to all
/// store providers (Postgres today, InMemory for tests, future variants).
/// </remarks>
public static class AuthHotpathCacheKeys
{
    /// <summary>Keyed registration of the concrete <see cref="IUserStore"/> implementation.</summary>
    public const string UserStoreInner = "auth.hotpath.user-store-inner";

    /// <summary>Keyed registration of the concrete <see cref="ITenantAuthConfigStore"/> implementation.</summary>
    public const string TenantAuthConfigStoreInner = "auth.hotpath.tenant-auth-config-store-inner";

    /// <summary>Redis pubsub channel name carrying invalidation messages between replicas.</summary>
    public const string PubSubChannel = "asterisk:auth:invalidate";
}
