using Microsoft.Extensions.Diagnostics.HealthChecks;
using Asterisk.Sdk.Hosting;

namespace Asterisk.Platform.Api.Health;

internal sealed class AsteriskAmiHealthCheck : IHealthCheck
{
    private readonly AsteriskOptions? _options;

    public AsteriskAmiHealthCheck(IServiceProvider services)
    {
        _options = services.GetService<Microsoft.Extensions.Options.IOptions<AsteriskOptions>>()?.Value;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // If AMI is not configured, report healthy (digital-only deployment)
        if (_options is null || string.IsNullOrWhiteSpace(_options.Ami?.Hostname))
            return Task.FromResult(HealthCheckResult.Healthy("Asterisk AMI not configured (digital-only mode)"));

        // AMI connectivity is checked via the AmiClient managed by SDK.
        // We report Degraded (not Unhealthy) since Platform can function without Asterisk for digital channels.
        // A full ping check would require an active AmiClient connection, which is managed by the SDK.
        return Task.FromResult(HealthCheckResult.Degraded(
            $"AMI configured at {_options.Ami.Hostname}:{_options.Ami.Port} — connectivity depends on SDK AmiClient"));
    }
}
