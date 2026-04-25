using Asterisk.Sdk.Pro.Push.SignalR.Authz;
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
/// attempt. Positive lookups are bounded by the same TTL — lateral
/// invalidation via the Pro.Push <c>AgentTenantMembershipChangedEvent</c>
/// (per ADR-0005) is deferred until that event type ships; until then the
/// cache reaches eventual consistency through TTL expiry.
/// </para>
/// </remarks>
public class CachedAgentTenantResolver : IAgentTenantResolver
{
    private const string CacheKeyPrefix = "agent-tenant:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedAgentTenantResolver> _logger;

    /// <summary>Creates a new resolver. Both dependencies are resolved from DI.</summary>
    public CachedAgentTenantResolver(
        NpgsqlDataSource dataSource,
        IMemoryCache cache,
        ILogger<CachedAgentTenantResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _cache = cache;
        _logger = logger;
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
