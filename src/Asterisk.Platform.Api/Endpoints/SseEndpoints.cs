using System.Text;
using System.Text.Json;
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
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        await context.Response.Body.FlushAsync(ct);

        // Send a heartbeat comment every 15 seconds to keep the connection alive.
        // Real event subscriptions would hook into IObservable event streams here.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                await context.Response.Body.WriteAsync(heartbeat, ct);
                await context.Response.Body.FlushAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static async Task WriteEventAsync(HttpResponse response, string eventType, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        var payload = $"event: {eventType}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
