using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Asterisk.Platform.Api.Serialization;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Endpoints;

internal static class SseEndpoints
{
    public static void MapSseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/events/stream", StreamEvents).RequireAuthorization();
    }

    private static async Task StreamEvents(
        HttpContext context,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(ct);

        var tenantId = context.Items.TryGetValue("TenantId", out var tid)
            ? tid as TenantId?
            : null;

        // Buffer events in a channel so the Rx subscription and the write loop are decoupled.
        var channel = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var subscription = eventBus.Events
            .Where(e => tenantId is null || e.TenantId == tenantId.Value.Value)
            .Subscribe(evt => channel.Writer.TryWrite(evt));

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = SendHeartbeatsAsync(context.Response, heartbeatCts.Token);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                await WriteEventAsync(context.Response, evt.Type, evt, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — expected.
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; }
            catch (OperationCanceledException) { }
        }
    }

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
