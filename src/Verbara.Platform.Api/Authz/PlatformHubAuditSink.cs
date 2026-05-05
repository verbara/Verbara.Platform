using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Push.SignalR.Authz;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Authz;

internal static partial class PlatformHubAuditSinkLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[AUTHZ/HUB-AUDIT] Failed to record entry action={Action}: {Reason}")]
    public static partial void RecordFailed(ILogger logger, string action, string reason);
}

/// <summary>
/// Bridges Pro.Push.SignalR's <see cref="IHubAuditSink"/> abstraction to the
/// Platform <see cref="IAuditService"/>. Used to persist
/// <c>hub.cross_tenant_subscription_denied</c> entries (per ADR-0005)
/// alongside the rest of Platform's audit trail so SOC operators / SIEM can
/// query a unified surface.
/// </summary>
public sealed class PlatformHubAuditSink : IHubAuditSink
{
    private const string Category = "auth";
    private const string Severity = "warning";
    private const string ActorType = "user";

    private readonly IAuditService _auditService;
    private readonly ILogger<PlatformHubAuditSink> _logger;

    /// <summary>Creates a new sink. Both dependencies are resolved from DI.</summary>
    public PlatformHubAuditSink(IAuditService auditService, ILogger<PlatformHubAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(logger);

        _auditService = auditService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteAsync(HubAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Dictionary<string, string>? metadata = null;
        if (!string.IsNullOrEmpty(entry.Metadata))
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["raw"] = entry.Metadata,
            };
        }

        try
        {
            await _auditService.RecordAsync(
                tenantId: new TenantId(entry.ActorTenantId),
                category: Category,
                action: entry.Action,
                severity: Severity,
                actorId: entry.ActorId,
                actorType: ActorType,
                targetId: entry.TargetId,
                targetType: "Agent",
                correlationId: null,
                changes: null,
                metadata: metadata,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlatformHubAuditSinkLog.RecordFailed(_logger, entry.Action, ex.Message);
            // Best-effort — never propagate to the hub method.
        }
    }
}
