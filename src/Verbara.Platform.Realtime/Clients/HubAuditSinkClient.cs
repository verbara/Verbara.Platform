using System.Net.Http.Json;
using Verbara.Platform.Realtime.Contracts;
using Verbara.Sdk.Pro.Push.SignalR.Authz;
using Microsoft.Extensions.Logging;
using WireHubAuditEntry = Verbara.Platform.Realtime.Contracts.Dtos.HubAuditEntry;

namespace Verbara.Platform.Realtime.Clients;

internal static partial class HubAuditSinkClientLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AUTHZ/HUB-AUDIT] HTTP post failed for action={Action}: {Reason}")]
    public static partial void PostFailed(ILogger logger, string action, string reason);
}

/// <summary>
/// <see cref="IHubAuditSink"/> implementation that forwards hub-level security
/// events to Platform.Api's <c>POST /api/v1/internal/hub-audit</c>. Fire-and-
/// forget — exceptions are swallowed and logged, never propagated back to the
/// hub method (the caller is already on the deny path).
/// </summary>
public sealed class HubAuditSinkClient : IHubAuditSink
{
    private const string HttpClientName = "platform-api-internal";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HubAuditSinkClient> _logger;

    public HubAuditSinkClient(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<HubAuditSinkClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task WriteAsync(HubAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var wire = new WireHubAuditEntry(
            Action: entry.Action,
            ActorTenantId: entry.ActorTenantId,
            ActorId: entry.ActorId,
            TargetId: entry.TargetId,
            Metadata: entry.Metadata,
            At: _timeProvider.GetUtcNow());

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                "api/v1/internal/hub-audit",
                wire,
                RealtimeContractsJsonContext.Default.HubAuditEntry,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            HubAuditSinkClientLog.PostFailed(_logger, entry.Action, ex.Message);
        }
    }
}
