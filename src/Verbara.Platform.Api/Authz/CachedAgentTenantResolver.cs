using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Platform.Api.Authz;

internal static partial class CachedAgentTenantResolverLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AUTHZ/AGENT-TENANT] Lookup failed for agent={AgentId}: {Reason}")]
    public static partial void LookupFailed(ILogger logger, string agentId, string reason);
}

/// <summary>
/// Postgres-backed (<c>agents.tenant_id</c>) lookup of an agent's owning
/// tenant with a 5-minute <see cref="IMemoryCache"/> in front. Consumed by
/// <see cref="Verbara.Platform.Api.Endpoints.Internal.InternalIntegrationEndpoints"/>
/// to serve the <c>GET /api/v1/internal/agent-tenant/{agentId}</c> contract
/// exposed to Verbara.Platform.Realtime.
/// </summary>
/// <remarks>
/// Pre-ADR-0022 this class implemented Pro.Push.SignalR's
/// <c>IAgentTenantResolver</c> and was consumed in-process by the Platform
/// Hub. After the Hub extraction, Realtime consumes the same data over HTTP
/// and the interface dependency was dropped — the cache + DB lookup logic is
/// otherwise unchanged.
/// </remarks>
public class CachedAgentTenantResolver
{
    private const string CacheKeyPrefix = "agent-tenant:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachedAgentTenantResolver> _logger;

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
            return null;
        }

        _cache.Set(key, tenantId, CacheDuration);
        return tenantId;
    }

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
