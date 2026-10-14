using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using Verbara.Platform.Api.Health;
using Verbara.Platform.Core.DependencyInjection;
using Verbara.Sdk.Pro.Storage.Common.Retention;
using Verbara.Sdk.Resilience;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The Api's health wiring (readiness-without-asterisk, design D6; contract Platform/ADR-0025): the health-check
/// registrations and the <c>/health</c> and <c>/health/ready</c> mapping, moved out of <c>Program.cs</c> because of
/// its LOC budget (<c>scripts/check-endpoint-invariants.py</c> gate #9). <c>Program.cs</c> spends one call on each,
/// at the positions the inline blocks held, so registration order and the predicate's view of the container are
/// unchanged.
/// </summary>
public static class PlatformHealthExtensions
{
    /// <summary>
    /// Registers the heartbeat registry, the resilience-state observer, the voice connection seam, the Postgres
    /// probe's policy and the Platform checks (<c>services</c>, <c>asterisk</c>, <c>retention</c>, and
    /// <c>postgres</c> when an <see cref="NpgsqlDataSource"/> is registered by the time this runs).
    /// </summary>
    public static IServiceCollection AddPlatformHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IServiceHeartbeat, ServiceHeartbeat>();

        // Frente D (v1.9.1): resilience-state-aware health checks. The observer listens
        // to the Verbara.Sdk.Resilience meter's circuit.state observable gauge and
        // caches per-policy-key state + timestamp so HealthCheck implementations can
        // distinguish Healthy / Degraded / Unhealthy based on how long a circuit has
        // been open.
        services.Configure<PlatformHealthCheckOptions>(_ => { });
        services.AddSingleton<ResilienceStateObserver>();
        services.AddSingleton<IResilienceStateObserver>(
            sp => sp.GetRequiredService<ResilienceStateObserver>());
        services.AddHostedService(
            sp => sp.GetRequiredService<ResilienceStateObserver>());

        // readiness-without-asterisk D2/D5: the one view of this pod's primary AMI connection, shared by the
        // asterisk check and the voice route gate. Construction touches no connection (defer-external-seams).
        services.AddSingleton<IVoiceConnectionStatus>(sp => new PrimaryAmiVoiceConnectionStatus(
            sp.GetRequiredService<Verbara.Sdk.Live.Server.VerbaraServerPool>(),
            sp.GetRequiredService<IConfiguration>()));

        // Dedicated HealthCheck-owned resilience policy: 2s timeout, no retry, no
        // circuit (we want every probe to run). Surfaces Postgres-under-load as
        // Unhealthy with a clear reason instead of hanging on the outer HealthCheck
        // timeout.
        services.AddKeyedSingleton<ResiliencePolicy>(
            PostgresHealthCheck.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithTimeout(TimeSpan.FromSeconds(2))
                .Build());

        // R5.3 A.5.b — promote 4 Pro BackgroundServices to concrete singletons so the
        // IHealthCheck implementations on those classes (added by Pro task A.5,
        // Pro 1.13.0-pro commit 05adeb8) can be resolved by the health-check
        // registry. Pro DI extensions register the services as
        // AddHostedService<T> only (binding T -> IHostedService directly, no
        // concrete singleton); without this swap, AddCheck<T>() below would resolve
        // a *second* instance whose heartbeat never ticks and would always report
        // Unhealthy. The pattern mirrors AnalyticsLiveServiceCollectionExtensions
        // (R5.1 LiveQueueSnapshotWriter): register T as Singleton, then forward
        // IHostedService via factory so both resolve to the same instance.
        // ADR-0022 Phase A — Presence health checks moved to Verbara.Platform.Realtime
        // along with the Pro.Push.SignalR services that host them. RetentionService
        // remains in Platform.Api.
        services
            .PromoteHostedServiceToSingleton<RetentionService>();

        var healthBuilder = services.AddHealthChecks()
            .AddCheck<BackgroundServiceHealthCheck>("services", tags: ["ready"])
            .AddCheck<AsteriskAmiHealthCheck>("asterisk", tags: ["ready"])
            .AddCheck<RetentionService>("retention", tags: ["ready"]);

        // Only add Postgres health check if NpgsqlDataSource is registered
        if (services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            healthBuilder.AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

        return services;
    }

    /// <summary>Maps <c>/health</c> (liveness) and <c>/health/ready</c> (readiness).</summary>
    public static IEndpointRouteBuilder MapPlatformHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // K8s liveness vs readiness contract:
        //   /health (liveness) → process-alive only. NO dependency checks. Returns
        //     200 OK in <1 ms as long as the .NET host is responsive. Kubernetes
        //     uses this to decide whether to RESTART the pod — restarting because a
        //     dependency (Postgres / Asterisk / Redis) is degraded never helps; it
        //     just amplifies the outage by cycling pods that would have recovered.
        //   /health/ready (readiness) → process AND its dependencies. Returns 200
        //     only if all "ready"-tagged checks pass. Kubernetes uses this to
        //     decide whether to ROUTE TRAFFIC to the pod. Failures stop traffic but
        //     leave the pod alive to recover.
        //
        // Before this split, both endpoints ran the FULL check suite (Postgres SQL
        // ping, Asterisk AMI ping, background-service heartbeats, retention). Under
        // burst load, the per-request /health latency exceeded the chart's default
        // `livenessProbe.timeoutSeconds: 1` and Kubernetes restart-cascaded pods
        // during otherwise-survivable traffic spikes (R5.5 Phase B-LK 2026-05-24:
        // 4 pod restarts during VU=1500 presence + 250-500 RPS bursts, evidence
        // `docs/operations/r55-blk-evidence/2026-05-24-v251-baseline/`).
        //
        // Predicate = _ => false → run NO checks; default formatter returns plain
        // text "Healthy" with HTTP 200. <1 ms response, immune to dependency state.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false,
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = HealthReportJsonWriter.WriteAsync,
        });

        return app;
    }
}
