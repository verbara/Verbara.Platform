using Asterisk.Sdk.Pro.Push.SignalR.Authz;
using Asterisk.Sdk.Pro.Push.SignalR.Events;
using Asterisk.Sdk.Push.Bus;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Asterisk.Platform.Api.Authz;

internal static partial class CachedAgentTenantResolverLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AUTHZ/AGENT-TENANT] Lookup failed for agent={AgentId}: {Reason}")]
    public static partial void LookupFailed(ILogger logger, string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[AUTHZ/AGENT-TENANT] Invalidated cache for agent={AgentId} (action={Action}, tenant={TenantId})")]
    public static partial void Invalidated(ILogger logger, string agentId, AgentTenantMembershipAction action, string tenantId);
}

/// <summary>
/// <see cref="IAgentTenantResolver"/> implementation backed by Postgres
/// (<c>agents.tenant_id</c>) with a 5-minute <see cref="IMemoryCache"/>
/// fronting the lookup. Implements ADR-0005 §"Resolver implementation".
/// </summary>
/// <remarks>
/// <para>
/// Negative lookups (agent unknown) are also cached for 5 minutes so a
/// pathological caller cannot pin one DB connection per failed enumeration
/// attempt. Positive lookups are bounded by the same TTL plus lateral
/// invalidation: the resolver subscribes to
/// <see cref="AgentTenantMembershipChangedEvent"/> on the Pro.Push backplane
/// and evicts the matching cache entry on receipt — closes the ADR-0005
/// §"Concerns" 5-min TTL gap (R5.3 Wave 2.5 task A.4.b).
/// </para>
/// <para>
/// The push subscription is held for the lifetime of the resolver instance
/// (singleton in DI) and disposed when the resolver is disposed. If the
/// Pro.Push backplane is unavailable or no event is ever published, behavior
/// degrades gracefully to TTL-only eviction.
/// </para>
/// </remarks>
public class CachedAgentTenantResolver : IAgentTenantResolver, IDisposable
{
    private const string CacheKeyPrefix = "agent-tenant:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedAgentTenantResolver> _logger;
    private readonly IDisposable? _membershipSubscription;
    private bool _disposed;

    /// <summary>Creates a new resolver. All dependencies are resolved from DI.</summary>
    public CachedAgentTenantResolver(
        NpgsqlDataSource dataSource,
        IMemoryCache cache,
        IPushEventBus eventBus,
        ILogger<CachedAgentTenantResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _cache = cache;
        _logger = logger;

        // Lateral invalidation: evict per-agent cache slot when Pro.Push
        // delivers an AgentTenantMembershipChangedEvent. Subscription lives
        // for the lifetime of the singleton resolver.
        _membershipSubscription = eventBus
            .OfType<AgentTenantMembershipChangedEvent>()
            .Subscribe(OnMembershipChanged);
    }

    /// <summary>Releases the Pro.Push subscription.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the Pro.Push subscription; override to extend.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
            _membershipSubscription?.Dispose();

        _disposed = true;
    }

    private void OnMembershipChanged(AgentTenantMembershipChangedEvent evt)
    {
        if (string.IsNullOrEmpty(evt.AgentId))
            return;

        _cache.Remove(CacheKeyPrefix + evt.AgentId);
        CachedAgentTenantResolverLog.Invalidated(_logger, evt.AgentId, evt.Action, evt.TenantId);
    }

    /// <inheritdoc />
    public async Task<string?> GetTenantIdAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);

        var key = CacheKeyPrefix + agentId;
        if (_cache.TryGetValue<string?>(key, out var cached))
            return cached;

        string? tenantId;
        try
        {
            tenantId = await LookupTenantIdAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CachedAgentTenantResolverLog.LookupFailed(_logger, agentId, ex.Message);
            // Don't cache lookup failures — let the next caller retry the DB.
            return null;
        }

        _cache.Set(key, tenantId, CacheDuration);
        return tenantId;
    }

    /// <summary>
    /// Performs the actual DB lookup. Marked virtual so unit tests can override
    /// without spinning up Postgres — the cache + error-handling logic is what
    /// this class adds and is fully covered by the override pattern.
    /// </summary>
    protected virtual async Task<string?> LookupTenantIdAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                "SELECT tenant_id FROM agents WHERE agent_id = @AgentId LIMIT 1",
                new { AgentId = agentId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
