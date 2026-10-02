using System.Reactive.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Identity;
using Verbara.Sdk.Push.Delivery;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class SseEndpoints
{
    public static void MapSseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/stream", StreamEvents).RequireAuthorization("Authenticated");
    }

    private static async Task StreamEvents(
        HttpContext context,
        PlatformEventBus eventBus,
        IEventDeliveryFilter deliveryFilter,
        [FromServices] IUserStore userStore,
        [FromServices] LiveConnectionRegistry liveConnections,
        [FromServices] TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Verbara.Platform.Api.Endpoints.Sse");

        // The stream is authenticated once, here, and then lives as long as the client keeps it
        // open — while the access token that opened it can outlive a suspension by up to its
        // lifetime. So: register the stream under its owner (before the status read, so a
        // revocation landing during the read still finds it), refuse an account that may not
        // authenticate, let UserAccessRevokedEvent end the stream later, and end it at the latest
        // when the credential that opened it expires — the bound for any revocation it misses.
        // A credential already past that point (JwtBearer grants a clock-skew grace) opens nothing:
        // the stream would end at once, and the client's reconnect would loop through it.
        var owner = LiveConnectionOwner.FromPrincipal(context.User);
        var expiresAt = LiveConnectionExpiry.Of(context.User);
        if (expiresAt <= time.GetUtcNow())
        {
            LogStreamRefusedExpired(logger, owner?.TenantId, owner?.UserId);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var liveRegistration = owner is { } o
            ? liveConnections.Register(o.TenantId, o.UserId, streamCts.Cancel)
            : null;
        if (owner is { } account && !await CanAuthenticateAsync(userStore, account, ct))
        {
            LogStreamRefused(logger, account.TenantId, account.UserId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Ending the stream is an ordinary end of the response: the client reconnects with the
        // credential it holds by then, and that request is authenticated and checked afresh.
        using var expiry = LiveConnectionExpiry.ScheduleClose(time, expiresAt, () =>
        {
            LogStreamExpired(logger, owner?.TenantId, owner?.UserId);
            streamCts.Cancel();
        }, logger);

        var streamToken = streamCts.Token;

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(streamToken);

        var tenantId = context.Items.TryGetValue("TenantId", out var tid)
            ? tid as TenantId?
            : null;
        var userId = context.User.FindFirst("sub")?.Value;

        LogClientConnected(logger, tenantId?.Value, userId);

        // Build the SubscriberContext used by the Sdk.Push delivery filter.
        // When TenantId is absent (anonymous / cross-tenant admin scenario) we fall
        // back to an empty string so the filter's tenant comparison evaluates against
        // a deterministic value — anonymous events without TenantId in their metadata
        // would still match (broadcast UserId == null path).
        var subscriberRoles = ExtractClaimSet(context.User, ClaimTypes.Role, "role");
        var subscriberPermissions = ExtractClaimSet(context.User, "permission");
        var subscriberContext = new SubscriberContext(
            TenantId: tenantId?.Value ?? string.Empty,
            UserId: userId,
            Roles: subscriberRoles,
            Permissions: subscriberPermissions);

        // Buffer events in a channel so the Rx subscription and the write loop are decoupled.
        var channel = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var subscription = eventBus.Events
            // The revocation signal ends streams (via the registry above); it is never content.
            .Where(e => e is not UserAccessRevokedEvent && deliveryFilter.IsDeliverableToSubscriber(
                e,
                tenantId is null
                    // Anonymous / unscoped stream: align subscriber tenant to the event's so the
                    // filter's tenant check passes; user-targeting still applies.
                    ? subscriberContext with { TenantId = e.Metadata.TenantId }
                    : subscriberContext))
            .Subscribe(evt =>
            {
                if (!channel.Writer.TryWrite(evt))
                {
                    LogEventBufferFull(logger, evt.Type);
                }
            });

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
        var heartbeatTask = SendHeartbeatsAsync(context.Response, logger, heartbeatCts.Token);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(streamToken))
            {
                try
                {
                    await WriteEventAsync(context.Response, evt.Type, evt, streamToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad event must not kill the stream.
                    LogEventWriteFailed(logger, evt.Type, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected, or the owner's access was revoked — expected either way.
        }
        finally
        {
            LogClientDisconnected(logger, tenantId?.Value, userId);
            channel.Writer.TryComplete();
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { LogHeartbeatCleanupFailed(logger, ex); }
        }
    }

    private static async Task<bool> CanAuthenticateAsync(
        IUserStore userStore, LiveConnectionOwner owner, CancellationToken ct)
    {
        var user = await userStore.GetByIdAsync(new TenantId(owner.TenantId), EntityId.From(owner.UserId), ct);
        return user is { CanAuthenticate: true };
    }

    /// <summary>
    /// Back-compat helper retained for existing unit tests. The runtime path now goes through
    /// <see cref="IEventDeliveryFilter"/> (<see cref="PlatformDeliveryFilter"/> by default), but
    /// this static surface keeps verifying the user-targeting semantics independent of DI wiring.
    /// </summary>
    internal static bool IsDeliverableToUser(PlatformEvent evt, string? userId)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Mirror PlatformDeliveryFilter without requiring tenant resolution.
        if (evt.Metadata.UserId is null)
        {
            return true;
        }
        return userId is not null && evt.Metadata.UserId == userId;
    }

    private static HashSet<string> ExtractClaimSet(ClaimsPrincipal user, params string[] claimTypes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in claimTypes)
        {
            foreach (var value in user.FindAll(type)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrEmpty(v)))
            {
                set.Add(value);
            }
        }
        return set;
    }

    [LoggerMessage(EventId = 7100, Level = LogLevel.Debug,
        Message = "SSE client connected (tenant={TenantId}, user={UserId})")]
    private static partial void LogClientConnected(ILogger logger, string? tenantId, string? userId);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Debug,
        Message = "SSE client disconnected (tenant={TenantId}, user={UserId})")]
    private static partial void LogClientDisconnected(ILogger logger, string? tenantId, string? userId);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning,
        Message = "SSE event write failed (type={EventType})")]
    private static partial void LogEventWriteFailed(ILogger logger, string eventType, Exception ex);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning,
        Message = "SSE heartbeat failed — stream may be broken")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning,
        Message = "SSE event buffer full, dropping oldest event (type={EventType})")]
    private static partial void LogEventBufferFull(ILogger logger, string eventType);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Debug,
        Message = "SSE heartbeat cleanup failed")]
    private static partial void LogHeartbeatCleanupFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Information,
        Message = "SSE stream refused: account is not active (tenant={TenantId}, user={UserId})")]
    private static partial void LogStreamRefused(ILogger logger, string tenantId, string userId);

    [LoggerMessage(EventId = 7107, Level = LogLevel.Information,
        Message = "SSE stream refused: the credential presented has expired (tenant={TenantId}, user={UserId})")]
    private static partial void LogStreamRefusedExpired(ILogger logger, string? tenantId, string? userId);

    [LoggerMessage(EventId = 7108, Level = LogLevel.Information,
        Message = "SSE stream ended: the credential that opened it expired (tenant={TenantId}, user={UserId}); the client may reconnect with a current one")]
    private static partial void LogStreamExpired(ILogger logger, string? tenantId, string? userId);

    private static async Task SendHeartbeatsAsync(HttpResponse response, ILogger logger, CancellationToken ct)
    {
        var heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                await response.Body.WriteAsync(heartbeat, ct);
                await response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogHeartbeatFailed(logger, ex);
                break;
            }
        }
    }

    internal static async Task WriteEventAsync(HttpResponse response, string eventType, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, data.GetType(), ApiJsonContext.Default);
        var payload = $"event: {eventType}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
