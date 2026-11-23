using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Reports, as Degraded, every stored tenant id that breaks the tenant-id rule and every <c>Realtime:TenantIds</c>
/// entry with no stored tenant (realtime-objects-stay-in-their-tenant, design D2). Never Unhealthy: readiness keeps
/// answering 200. Evaluated on each probe, never at host start.
/// </summary>
internal sealed class TenantIdHealthCheck : IHealthCheck
{
    /// <summary>The check's registration name.</summary>
    public const string Name = "tenant-ids";

    /// <summary>What an operator reads when the check is Degraded.</summary>
    public const string DegradedDescription =
        "Stored tenant ids break the tenant-id rule, or configured realtime tenants do not exist. A flagged tenant keeps " +
        "the calls already provisioned, is not reconciled, its suspension or deletion removes its credentials, queue " +
        "memberships and trunks by exact id, and its queues are never removed. A flagged pair stays in that state, even " +
        "after one of the two is deleted, until a tenant rename tool is available; nothing else resolves it.";

    private readonly TenantIdAudit _audit;
    private readonly IConfiguration _configuration;

    public TenantIdHealthCheck(TenantIdAudit audit, IConfiguration configuration)
    {
        _audit = audit;
        _configuration = configuration;
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
            return HealthCheckResult.Degraded("The stored tenant ids could not be read.", ex);
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var paired = new HashSet<(string, string)>();
        foreach (var p in report.Pairs)
        {
            data[$"pair:{p.Shorter.TenantId}/{p.Longer.TenantId}"] =
                $"{p.Shorter.TenantId} / {p.Longer.TenantId}: {p.Reason} ({p.Shorter.TenantId} {p.Shorter.Status}, {p.Longer.TenantId} {p.Longer.Status})";
            paired.Add((p.Shorter.TenantId, p.Reason));
            paired.Add((p.Longer.TenantId, p.Reason));
        }
        foreach (var f in report.Flags.Where(f => !paired.Contains((f.TenantId, f.Reason))))
            data[$"flag:{f.TenantId}:{f.Reason}"] = $"{f.TenantId}: {f.Reason} ({f.Status})";
        foreach (var id in PlatformDesiredStateProvider.ReadConfiguredTenantIds(_configuration))
        {
            if (report.Find(id) is null)
                data[$"realtime:{id}"] = $"configured realtime tenant '{id}' does not exist";
        }

        return data.Count == 0
            ? HealthCheckResult.Healthy("No stored tenant id breaks the tenant-id rule.")
            : HealthCheckResult.Degraded(DegradedDescription, data: data);
    }
}
