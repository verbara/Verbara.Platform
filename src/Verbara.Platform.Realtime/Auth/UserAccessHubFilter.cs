using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Clients;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Auth;

/// <summary>
/// Enforces Platform.Api's account-status rule on hub connections. The JWT is validated once, at
/// connect, and can outlive a suspension by up to its lifetime; this filter makes one status lookup
/// per connection, registers every admitted connection so <see cref="UserAccessRevokedEvent"/>
/// — delivered to every pod — aborts it, and closes every admitted connection when the token that
/// opened it expires.
/// </summary>
/// <remarks>
/// <para>
/// A refusal throws from <c>OnConnectedAsync</c>: SignalR then closes the connection with
/// <c>allowReconnect: false</c>, which stops the client's automatic-reconnect loop. A revocation
/// aborts the connection, which SignalR closes the same way. Without the lookup, either would be
/// undone the next time the client connects with an access token that is still valid — the Web
/// client opens a new hub connection whenever its token is refreshed.
/// </para>
/// <para>
/// Every admitted connection is closed when its token expires (<see cref="LiveConnectionExpiry"/>),
/// whatever the lookup answered. A connection the Api vouched for can still miss the revocation
/// that should end it — the event lost on the way to this pod, the status read from a stale cache,
/// sessions revoked for an account that is still active — and nothing else would end it: SignalR
/// does not close a connection when its token expires. The lookup also fails open when the Api
/// gives no answer (<see cref="UserAccessVerdict.Unavailable"/>), for an account whose revocation
/// may have fired before this connect; the same close bounds that.
/// </para>
/// <para>
/// That close is a close request, not an abort: SignalR then tells the client it may reconnect,
/// and the reconnect presents the token the client holds by then, which authentication and the
/// lookup judge afresh. It is scheduled once the hub has run its own <c>OnConnectedAsync</c>: a
/// close landing inside it can make it throw, and SignalR answers that with
/// <c>allowReconnect: false</c>.
/// </para>
/// <para>
/// A principal that names no account is refused: every token Platform.Api mints carries one.
/// </para>
/// </remarks>
internal sealed partial class UserAccessHubFilter : IHubFilter
{
    private const string TrackingItem = "verbara.user_access.tracking";
    private const string NotActiveMessage = "Account is not active.";

    private readonly LiveConnectionRegistry _connections;
    private readonly IUserAccessStatusClient _status;
    private readonly TimeProvider _time;
    private readonly ILogger<UserAccessHubFilter> _logger;

    public UserAccessHubFilter(
        LiveConnectionRegistry connections,
        IUserAccessStatusClient status,
        TimeProvider time,
        ILogger<UserAccessHubFilter> logger)
    {
        _connections = connections;
        _status = status;
        _time = time;
        _logger = logger;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var caller = context.Context;
        if (LiveConnectionOwner.FromPrincipal(caller.User) is not { } owner)
        {
            LogRefusedUnattributed(_logger, caller.ConnectionId);
            throw new HubException(NotActiveMessage);
        }

        // Register before the lookup: a revocation that lands while it is in flight still finds
        // this connection; one that landed before it is caught by the lookup itself, because the
        // Api persists the new status before announcing the revocation.
        var tracking = new ConnectionTracking(_connections.Register(owner.TenantId, owner.UserId, caller.Abort));
        try
        {
            var verdict = await _status.CheckAsync(owner.TenantId, owner.UserId, caller.ConnectionAborted)
                .ConfigureAwait(false);
            if (verdict == UserAccessVerdict.Denied)
            {
                LogRefused(_logger, owner.TenantId, owner.UserId);
                throw new HubException(NotActiveMessage);
            }

            var expiresAt = LiveConnectionExpiry.Of(caller.User);
            if (verdict == UserAccessVerdict.Unavailable)
                LogAdmittedUnchecked(_logger, owner.TenantId, owner.UserId, expiresAt);

            caller.Items[TrackingItem] = tracking;
            await next(context).ConfigureAwait(false);

            tracking.Expiry = LiveConnectionExpiry.ScheduleClose(
                _time, expiresAt, () => CloseAtTokenExpiry(caller, owner), _logger);
        }
        catch
        {
            // SignalR does not run OnDisconnectedAsync for a connection whose OnConnectedAsync
            // threw, so a refused or failed connect must leave the registry (and drop its expiry) here.
            caller.Items.Remove(TrackingItem);
            tracking.Dispose();
            throw;
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        context.Context.Items.Remove(TrackingItem, out var tracking);
        using (tracking as IDisposable)
        {
            await next(context, exception).ConfigureAwait(false);
        }
    }

    private void CloseAtTokenExpiry(HubCallerContext caller, LiveConnectionOwner owner)
    {
        LogClosedAtTokenExpiry(_logger, owner.TenantId, owner.UserId);

        // A close request makes SignalR tell the client it may reconnect; Abort() would tell it to
        // stay away — the answer for an account that may no longer connect, not for an expired
        // token. A transport that cannot take the request is aborted: the close itself is not optional.
        if (caller.Features.Get<IConnectionLifetimeNotificationFeature>() is { } lifetime)
            lifetime.RequestClose();
        else
            caller.Abort();
    }

    /// <summary>What the filter holds for an admitted connection until it disconnects.</summary>
    private sealed class ConnectionTracking(IDisposable registration) : IDisposable
    {
        /// <summary>The close scheduled at the expiry of the token that opened the connection.</summary>
        public ITimer? Expiry { get; set; }

        public void Dispose()
        {
            Expiry?.Dispose();
            registration.Dispose();
        }
    }

    [LoggerMessage(EventId = 7310, Level = LogLevel.Information,
        Message = "[AUTHZ/USER-ACCESS] Hub connection refused: account is not active (user={UserId} tenant={TenantId})")]
    private static partial void LogRefused(ILogger logger, string tenantId, string userId);

    [LoggerMessage(EventId = 7312, Level = LogLevel.Warning,
        Message = "[AUTHZ/USER-ACCESS] Hub connection admitted without an account-status answer (user={UserId} tenant={TenantId}); it is closed when its access token expires at {ExpiresAt:O}")]
    private static partial void LogAdmittedUnchecked(ILogger logger, string tenantId, string userId, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 7313, Level = LogLevel.Warning,
        Message = "[AUTHZ/USER-ACCESS] Hub connection refused: token names no account (conn={ConnectionId})")]
    private static partial void LogRefusedUnattributed(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 7314, Level = LogLevel.Information,
        Message = "[AUTHZ/USER-ACCESS] Hub connection closed: the access token that opened it expired (user={UserId} tenant={TenantId}); the client may reconnect with a current one")]
    private static partial void LogClosedAtTokenExpiry(ILogger logger, string tenantId, string userId);
}
