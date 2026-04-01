using Asterisk.Platform.Core.Webhooks;

namespace Asterisk.Platform.Api.Endpoints;

internal static class WebhookEventTypeEndpoints
{
    public static void MapWebhookEventTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks/event-types").RequireAuthorization("Authenticated");
        group.MapGet("/", ListEventTypes);
    }

    private static IResult ListEventTypes()
    {
        var types = WebhookEventTypes.All.Select(t => new WebhookEventTypeDto(
            t,
            WebhookEventTypes.Descriptions.TryGetValue(t, out var desc) ? desc : ""));
        return Results.Ok(types.ToList());
    }
}

internal sealed record WebhookEventTypeDto(string EventType, string Description);
