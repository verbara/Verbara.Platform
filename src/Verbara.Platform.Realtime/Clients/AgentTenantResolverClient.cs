using System.Net.Http.Json;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Sdk.Pro.Push.SignalR.Authz;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Clients;

internal static partial class AgentTenantResolverClientLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AUTHZ/AGENT-TENANT] HTTP lookup failed for agent={AgentId}: {Reason}")]
    public static partial void LookupFailed(ILogger logger, string agentId, string reason);
}

/// <summary>
/// <see cref="IAgentTenantResolver"/> implementation backed by an HTTP call to
/// Platform.Api's <c>GET /api/v1/internal/agent-tenant/{agentId}</c>. A
/// 5-minute <see cref="IMemoryCache"/> fronts the call so the Hub authz path
/// never blocks on a network round-trip for cached agents (the same TTL the
/// in-process CachedAgentTenantResolver used pre-ADR-0022).
/// </summary>
public sealed class AgentTenantResolverClient : IAgentTenantResolver
{
    private const string CacheKeyPrefix = "agent-tenant:";
    private const string HttpClientName = "platform-api-internal";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AgentTenantResolverClient> _logger;

    public AgentTenantResolverClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<AgentTenantResolverClient> logger)
    {
        _httpClientFactory = httpClientFactory;
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
            AgentTenantResolverClientLog.LookupFailed(_logger, agentId, ex.Message);
            return null;
        }

        _cache.Set(key, tenantId, CacheDuration);
        return tenantId;
    }

    private async Task<string?> LookupTenantIdAsync(string agentId, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(
            $"api/v1/internal/agent-tenant/{Uri.EscapeDataString(agentId)}",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();

        var payload = await response.Content
            .ReadFromJsonAsync(RealtimeContractsJsonContext.Default.AgentTenantResponse, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(payload?.TenantId) ? null : payload.TenantId;
    }
}
