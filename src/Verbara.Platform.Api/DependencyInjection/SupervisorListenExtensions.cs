using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.OutboundTrunk;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// supervisor-silent-listen — the composition-root entry point of the change (design D12: <c>Program.cs</c> spends a
/// single call on it, inside the <c>voiceAmiEnabled</c> block). Without AMI nothing here is registered, the routes stay
/// mapped, and start, stop and current answer <c>voice-unavailable</c>. The listen-endpoint route, the admin listen
/// phone and the desired-state slots need none of this: the profile store is registered by the storage layer.
/// </summary>
public static class SupervisorListenExtensions
{
    /// <summary>Registers the silent listen: its executor, registry, audit, AMI observer and service.</summary>
    public static IServiceCollection AddSupervisorListen(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSupervisorListenOriginateExecutor();
        services.AddDialableTrunkResolver(configuration);
        services.TryAddSingleton<SupervisorListenRegistry>();
        services.TryAddSingleton(sp => new SupervisorListenAudit(
            sp.GetRequiredService<IAuditService>(), sp.GetRequiredService<ILogger<SupervisorListenAudit>>()));
        services.TryAddSingleton<IVoiceCallLegs>(sp => new CallSessionLegs(sp.GetRequiredService<ICallSessionManager>()));
        // The observer subscribes to AMI on first use only (defer-external-seams): resolving it touches nothing.
        services.TryAddSingleton(sp => new SupervisorListenObserver(
            sp.GetRequiredService<VerbaraServerPool>(),
            sp.GetRequiredService<SupervisorListenRegistry>(),
            sp.GetRequiredService<SupervisorListenAudit>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<SupervisorListenObserver>>()));
        services.TryAddSingleton(sp => new SupervisorListenService(
            sp.GetRequiredService<IConversationStore>(),
            sp.GetRequiredService<ISupervisorListenProfileStore>(),
            sp.GetRequiredService<IVoiceCallLegs>(),
            sp.GetRequiredKeyedService<OriginateExecutorBase>(SupervisorListenOriginateExecutor.ServiceKey),
            sp.GetRequiredService<DialableTrunkResolver>(),
            sp.GetRequiredService<ITenantStore>(),
            sp.GetRequiredKeyedService<IClusterLeader>(VoiceLeaderResources.AmiOwner),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<SupervisorListenRegistry>(),
            sp.GetRequiredService<SupervisorListenAudit>(),
            sp.GetRequiredService<SupervisorListenObserver>(),
            sp.GetRequiredService<ILogger<SupervisorListenService>>()));
        return services;
    }

    /// <summary>
    /// Registers the listen's own originate executor under <see cref="SupervisorListenOriginateExecutor.ServiceKey"/>
    /// (design D2). It is keyed, so it never replaces the unkeyed Dialer executor click-to-dial resolves.
    /// </summary>
    public static IServiceCollection AddSupervisorListenOriginateExecutor(this IServiceCollection services)
    {
        services.AddKeyedSingleton<OriginateExecutorBase>(SupervisorListenOriginateExecutor.ServiceKey,
            (sp, _) => new SupervisorListenOriginateExecutor(sp.GetRequiredService<VerbaraServerPool>()));
        return services;
    }
}
