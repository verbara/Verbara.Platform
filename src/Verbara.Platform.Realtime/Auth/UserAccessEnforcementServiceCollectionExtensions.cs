using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Clients;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Verbara.Platform.Realtime.Auth;

/// <summary>DI wiring that enforces Platform.Api's account-status rule on hub connections.</summary>
internal static class UserAccessEnforcementServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="UserAccessHubFilter"/> as a global hub filter (one status lookup per
    /// connection, and a close at the expiry of the token that opened it), the Platform.Api lookup
    /// it calls, and the live-connection registry plus the revocation listener that abort a user's
    /// connections on this pod when <c>UserAccessRevokedEvent</c> arrives. Requires an
    /// <c>IPushEventBus</c>.
    /// </summary>
    public static IServiceCollection AddUserAccessEnforcement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLiveConnectionRevocation();
        services.TryAddSingleton<IUserAccessStatusClient, UserAccessStatusClient>();
        // Schedules the close of every admitted connection at its token's expiry.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UserAccessHubFilter>();
        services.AddSignalR(o => o.AddFilter<UserAccessHubFilter>());
        return services;
    }
}
