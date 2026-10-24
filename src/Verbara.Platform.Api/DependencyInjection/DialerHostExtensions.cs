using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Dialer;
using Verbara.Sdk.Pro.Dialer.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Storage.Postgres.Retention;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The Api's Pro.Dialer wiring (dialer-places-calls-end-to-end, design D7), moved out of <c>Program.cs</c> because
/// of its LOC budget (<c>scripts/check-endpoint-invariants.py</c> gate #9). <c>Program.cs</c> keeps the
/// connection-string read, which it uses again further down, and spends one call here at the position the inline
/// block held, so registration order is unchanged. Precedents: <see cref="ClusterLeadershipExtensions"/> and
/// <see cref="PlatformHealthExtensions"/>.
/// </summary>
public static class DialerHostExtensions
{
    /// <summary>
    /// Registers the Postgres dialer storage on <paramref name="dialerDataSource"/>, the Pro dialer engine with the
    /// options <see cref="ConfigureDialerOptions"/> maps from <paramref name="configuration"/>, the dialer retention
    /// targets and the <see cref="CampaignMetricsPoller"/>.
    /// </summary>
    public static IServiceCollection AddPlatformDialer(
        this IServiceCollection services,
        IConfiguration configuration,
        NpgsqlDataSource dialerDataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(dialerDataSource);

        services.UsePostgresDialerStorage(dialerDataSource);
        services.AddProDialer(o => ConfigureDialerOptions(o, configuration));
        services.AddDialerRetentionTargets();
        services.AddHostedService<CampaignMetricsPoller>();
        return services;
    }

    /// <summary>
    /// Maps the Api's configuration onto the Pro dialer's options. It is public so the host tests can assert the
    /// mapping without building a host.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The node is <see cref="AsteriskNodeIds.Primary"/>, the key the cluster registers the Api's Asterisk
    /// server under, so every activation originates on the node that exists (design D1). Pro's own default,
    /// <c>"default"</c>, names no node here.</item>
    /// <item>The default trunk name is <c>Asterisk:Outbound:DefaultTrunk</c>, the key
    /// <see cref="OutboundTrunkExtensions.AddDialableTrunkResolver"/> reads, so a campaign with no matching route and
    /// no default trunk of its own falls back to the trunk the Api's other calls use (design D2). Unset or blank, it
    /// leaves that tier empty, as on the Api's side.</item>
    /// </list>
    /// </remarks>
    public static void ConfigureDialerOptions(DialerOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        options.NodeId = AsteriskNodeIds.Primary;
        options.DefaultTrunkName = configuration["Asterisk:Outbound:DefaultTrunk"];
    }
}
