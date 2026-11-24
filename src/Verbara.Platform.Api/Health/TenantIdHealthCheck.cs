using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Reports, as Degraded, how many stored tenant ids break the tenant-id rule and how many <c>Realtime:TenantIds</c>
/// entries have no stored tenant (realtime-objects-stay-in-their-tenant, design D2). Never Unhealthy: readiness keeps
/// answering 200. Evaluated on each probe, never at host start. Readiness is anonymous, so the report carries counts
/// only; the ids, their statuses and the pairs go to the Api log at Warning, once each time the flagged set changes.
/// Registered as a singleton so that last-logged set survives between probes.
/// </summary>
internal sealed partial class TenantIdHealthCheck : IHealthCheck
{
    /// <summary>The check's registration name.</summary>
    public const string Name = "tenant-ids";

    /// <summary>What an operator reads in the Api log when the check is Degraded.</summary>
    public const string OperatorGuidance =
        "A flagged tenant keeps the calls already provisioned, is not reconciled, its suspension or deletion removes its " +
        "credentials, queue memberships and trunks by exact id, and its queues are never removed. A flagged pair stays " +
        "in that state, even after one of the two is deleted, until a tenant rename tool is available; nothing else " +
        "resolves it.";

    private readonly TenantIdAudit _audit;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantIdHealthCheck> _logger;
    private readonly Lock _gate = new();
    private string? _lastLogged;

    public TenantIdHealthCheck(TenantIdAudit audit, IConfiguration configuration, ILogger<TenantIdHealthCheck> logger)
    {
        _audit = audit;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        TenantIdAuditReport report;
        try
        {
            report = await _audit.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditUnreadable(_logger, ex);
            return HealthCheckResult.Degraded("The stored tenant ids could not be read; see the Api log.");
        }

        var details = new List<string>();
        var paired = new HashSet<(string, string)>();
        foreach (var p in report.Pairs)
        {
            details.Add($"{p.Shorter.TenantId} / {p.Longer.TenantId}: {p.Reason} ({p.Shorter.TenantId} {p.Shorter.Status}, {p.Longer.TenantId} {p.Longer.Status})");
            paired.Add((p.Shorter.TenantId, p.Reason));
            paired.Add((p.Longer.TenantId, p.Reason));
        }
        foreach (var f in report.Flags.Where(f => !paired.Contains((f.TenantId, f.Reason))))
            details.Add($"{f.TenantId}: {f.Reason} ({f.Status})");
        var unstored = 0;
        foreach (var id in PlatformDesiredStateProvider.ReadConfiguredTenantIds(_configuration).Where(id => report.Find(id) is null))
        {
            unstored++;
            details.Add($"configured realtime tenant '{id}' does not exist");
        }

        var flagged = report.Flags.Select(f => f.TenantId).Distinct(StringComparer.Ordinal).Count();
        LogOnChange(details);
        if (details.Count == 0)
            return HealthCheckResult.Healthy("No stored tenant id breaks the tenant-id rule.");

        var parts = new List<string>(2);
        if (flagged > 0)
            parts.Add(flagged == 1
                ? "1 stored tenant id breaks the tenant-id rule; see the Api log."
                : string.Create(CultureInfo.InvariantCulture, $"{flagged} stored tenant ids break the tenant-id rule; see the Api log."));
        if (unstored > 0)
            parts.Add(unstored == 1
                ? "1 configured realtime tenant id is not stored; see the Api log."
                : string.Create(CultureInfo.InvariantCulture, $"{unstored} configured realtime tenant ids are not stored; see the Api log."));
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["flaggedTenantIds"] = flagged,
            ["pairs"] = report.Pairs.Count,
            ["unstoredRealtimeTenantIds"] = unstored,
        };
        return HealthCheckResult.Degraded(string.Join(' ', parts), data: data);
    }

    private void LogOnChange(List<string> details)
    {
        details.Sort(StringComparer.Ordinal);
        var current = string.Join("; ", details);
        lock (_gate)
        {
            if (string.Equals(current, _lastLogged, StringComparison.Ordinal))
                return;
            _lastLogged = current;
        }
        if (details.Count > 0)
            LogFlagged(_logger, current, OperatorGuidance);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored tenant ids break the tenant-id rule: {Details}. {Guidance}")]
    private static partial void LogFlagged(ILogger logger, string details, string guidance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored tenant ids could not be read for the tenant-id health check")]
    private static partial void LogAuditUnreadable(ILogger logger, Exception ex);
}
