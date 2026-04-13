using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static partial class SseEndpoints
{
    public static void MapSseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/events/stream", StreamEvents).RequireAuthorization("Authenticated");
    }

    private static async Task StreamEvents(
        HttpContext context,
        PlatformEventBus eventBus,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Asterisk.Platform.Api.Endpoints.Sse");

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(ct);

        var tenantId = context.Items.TryGetValue("TenantId", out var tid)
            ? tid as TenantId?
            : null;
        var userId = context.User.FindFirst("sub")?.Value;

        LogClientConnected(logger, tenantId?.Value, userId);

        // Buffer events in a channel so the Rx subscription and the write loop are decoupled.
        var channel = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var subscription = eventBus.Events
            .Where(e => tenantId is null || e.TenantId == tenantId.Value.Value)
            .Where(e => IsDeliverableToUser(e, userId))
            .Subscribe(evt => channel.Writer.TryWrite(evt));

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = SendHeartbeatsAsync(context.Response, heartbeatCts.Token);

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
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// User-scoped events (currently <see cref="NotificationEvent"/>) are delivered only to the
    /// target user's SSE stream. Tenant-scoped events pass through to all subscribers in the tenant.
    /// </summary>
    internal static bool IsDeliverableToUser(PlatformEvent evt, string? userId)
    {
        if (evt is NotificationEvent notification)
        {
            return userId is not null && notification.UserId == userId;
        }
        return true;
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

    private static async Task SendHeartbeatsAsync(HttpResponse response, CancellationToken ct)
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
