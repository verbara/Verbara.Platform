using Microsoft.Extensions.Diagnostics.HealthChecks;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Health;

/// <summary>
/// Reports, as Degraded, every stored tenant id that breaks the tenant-id rule and every <c>Realtime:TenantIds</c>
/// entry with no stored tenant (realtime-objects-stay-in-their-tenant, design D2). Never Unhealthy.
/// </summary>
internal sealed class TenantIdHealthCheck : IHealthCheck
{
    private readonly TenantIdAudit _audit;
    private readonly IConfiguration _configuration;

    public TenantIdHealthCheck(TenantIdAudit audit, IConfiguration configuration)
    {
        _audit = audit;
        _configuration = configuration;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
