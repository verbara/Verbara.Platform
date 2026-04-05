using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class ManagementWebhookEndpoints
{
    public static void MapManagementWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/webhooks").RequireAuthorization("PlatformAdminOnly");
        group.MapGet("/dead-letter", ListDeadLetter);
        group.MapPost("/dead-letter/{id}/retry", RetryDeadLetter);
    }

    private static async Task<IResult> ListDeadLetter(
        [FromQuery] string tenantId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] IWebhookDeliveryStore store,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return Results.BadRequest(new ErrorResponse("tenantId query parameter is required"));

        var p = page > 0 ? page : 1;
        var ps = pageSize > 0 ? Math.Min(pageSize, 100) : 20;

        var result = await store.ListDeadLetterAsync(tenantId, p, ps, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RetryDeadLetter(
        string id,
        [FromServices] IWebhookDeliveryStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var delivery = await store.GetByIdAsync(id, ct);
        if (delivery is null)
            return Results.NotFound();

        if (delivery.Status != WebhookDeliveryStatus.DeadLetter)
            return Results.BadRequest(new ErrorResponse("Only dead-letter deliveries can be retried"));

        var retried = delivery with
        {
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            MaxAttempts = 8,
            NextRetryAt = clock.UtcNow,
            LastError = null,
            LastResponseCode = null,
            DeliveredAt = null,
        };

        await store.UpdateAsync(retried, ct);
        return Results.Ok(new MessageResponse($"Delivery {id} re-enqueued for retry"));
    }
}
