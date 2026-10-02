using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Core.Push;

/// <summary>
/// Cuts an account's live connections on this node when a <see cref="UserAccessRevokedEvent"/>
/// arrives — published here, re-published by Realtime's dispatcher, or delivered as a backplane
/// envelope from another node.
/// </summary>
/// <remarks>
/// Runs on every node, never leader-gated: each node can only abort the connections it holds.
/// The push bus delivers on its own loop; an abort failure is logged and never escapes into it.
/// </remarks>
public sealed partial class UserAccessRevocationListener : IHostedService, IDisposable
{
    private readonly IPushEventBus _bus;
    private readonly LiveConnectionRegistry _connections;
    private readonly ILogger<UserAccessRevocationListener> _logger;
    private IDisposable? _subscription;

    public UserAccessRevocationListener(
        IPushEventBus bus,
        LiveConnectionRegistry connections,
        ILogger<UserAccessRevocationListener> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);

        _bus = bus;
        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.AsObservable().Subscribe(OnEvent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private void OnEvent(PushEvent pushEvent)
    {
        if (!UserAccessRevokedEvent.TryRead(pushEvent, out var revoked))
            return;

        try
        {
            var aborted = _connections.AbortAll(revoked.TenantId, revoked.UserId);
            if (aborted > 0)
                LogConnectionsAborted(_logger, aborted, revoked.TenantId, revoked.UserId, revoked.Reason);
        }
        catch (AggregateException ex)
        {
            // AbortAll's only failure mode, raised after every other connection was aborted.
            LogAbortFailed(_logger, revoked.TenantId, revoked.UserId, ex);
        }
    }

    [LoggerMessage(EventId = 7300, Level = LogLevel.Information,
        Message = "Aborted {Count} live connection(s) of user {UserId} in tenant {TenantId}: access revoked ({Reason}).")]
    private static partial void LogConnectionsAborted(ILogger logger, int count, string tenantId, string userId, string reason);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Error,
        Message = "Failed to abort one or more live connections of user {UserId} in tenant {TenantId} after access was revoked.")]
    private static partial void LogAbortFailed(ILogger logger, string tenantId, string userId, Exception exception);
}

/// <summary>DI wiring for live-connection revocation.</summary>
public static class LiveConnectionRevocationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the node's <see cref="LiveConnectionRegistry"/> and the
    /// <see cref="UserAccessRevocationListener"/> that drains it on revocation. Idempotent.
    /// </summary>
    public static IServiceCollection AddLiveConnectionRevocation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LiveConnectionRegistry>();
        services.AddHostedService<UserAccessRevocationListener>();
        return services;
    }
}
