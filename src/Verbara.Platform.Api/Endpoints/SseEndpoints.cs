using System.Reactive.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Sdk.Push.Delivery;

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
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Verbara.Platform.Api.Endpoints.Sse");

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(ct);

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
            .Where(e => deliveryFilter.IsDeliverableToSubscriber(
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

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = SendHeartbeatsAsync(context.Response, logger, heartbeatCts.Token);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await WriteEventAsync(context.Response, evt.Type, evt, ct);
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
            // Client disconnected — expected.
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
