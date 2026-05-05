using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class WebhookSubscriptionEndpoints
{
    public static void MapWebhookSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks/subscriptions")
            .RequireAuthorization("Authenticated")
            .RequirePlanFeature(PlanFeature.Webhooks);
        group.MapGet("/", ListSubscriptions);
        group.MapPost("/", CreateSubscription);
        group.MapGet("/{id}", GetSubscription);
        group.MapPut("/{id}", UpdateSubscription);
        group.MapDelete("/{id}", DeleteSubscription);
        group.MapPost("/{id}/test", TestSubscription);
        group.MapGet("/{id}/deliveries", ListDeliveries);
        group.MapPost("/{id}/rotate-secret", RotateSecret);
        group.MapPost("/{id}/reset-circuit", ResetCircuit);
        group.MapGet("/{id}/circuit-status", GetCircuitStatus);
    }

    private static async Task<IResult> ListSubscriptions(
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var subs = await store.ListByTenantAsync(tenantId, ct);
        return Results.Ok(subs.Select(MaskSecret).ToList());
    }

    private static async Task<IResult> CreateSubscription(
        HttpContext context,
        [FromBody] CreateWebhookSubscriptionRequest body,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new ErrorResponse("Name is required"));

        if (string.IsNullOrWhiteSpace(body.EndpointUrl))
            return Results.BadRequest(new ErrorResponse("EndpointUrl is required"));

        if (!Uri.TryCreate(body.EndpointUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ErrorResponse("EndpointUrl must be a valid HTTPS URL"));

        if (body.EventTypes is null || body.EventTypes.Count == 0)
            return Results.BadRequest(new ErrorResponse("At least one event type is required"));

        var invalid = body.EventTypes.Where(e => !WebhookEventTypes.IsValid(e)).ToList();
        if (invalid.Count > 0)
            return Results.BadRequest(new ErrorDetailResponse(
                "Invalid event types", invalid));

        var tenantId = GetTenantId(context);
        var now = clock.UtcNow;
        var subscription = new WebhookSubscription(
            SubscriptionId: Guid.NewGuid().ToString("N"),
            TenantId: tenantId,
            Name: body.Name,
            EndpointUrl: body.EndpointUrl,
            Secret: WebhookSignatureService.GenerateSecret(),
            EventTypes: body.EventTypes,
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now);

        await store.SaveAsync(subscription, ct);

        // Return with secret visible on creation only
        return Results.Created(
            $"/api/webhooks/subscriptions/{subscription.SubscriptionId}",
            subscription);
    }

    private static async Task<IResult> GetSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Ok(MaskSecret(sub));
    }

    private static async Task<IResult> UpdateSubscription(
        string id,
        HttpContext context,
        [FromBody] UpdateWebhookSubscriptionRequest body,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        if (body.EndpointUrl is not null
            && (!Uri.TryCreate(body.EndpointUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new ErrorResponse("EndpointUrl must be a valid HTTPS URL"));

        if (body.EventTypes is not null)
        {
            if (body.EventTypes.Count == 0)
                return Results.BadRequest(new ErrorResponse("At least one event type is required"));

            var invalid = body.EventTypes.Where(e => !WebhookEventTypes.IsValid(e)).ToList();
            if (invalid.Count > 0)
                return Results.BadRequest(new ErrorDetailResponse(
                    "Invalid event types", invalid));
        }

        var updated = sub with
        {
            Name = body.Name ?? sub.Name,
            EndpointUrl = body.EndpointUrl ?? sub.EndpointUrl,
            EventTypes = body.EventTypes ?? sub.EventTypes,
            IsActive = body.IsActive ?? sub.IsActive,
            UpdatedAt = clock.UtcNow,
        };

        await store.SaveAsync(updated, ct);
        return Results.Ok(MaskSecret(updated));
    }

    private static async Task<IResult> DeleteSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        await deliveryStore.DeleteBySubscriptionAsync(id, ct);
        await store.DeleteAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> TestSubscription(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var now = clock.UtcNow;
        var testPayload = JsonSerializer.Serialize(new WebhookEventPayload(
            Id: Guid.NewGuid().ToString("N"),
            Type: WebhookEventTypes.WebhookTest,
            TenantId: tenantId,
            Timestamp: now,
            Data: new Dictionary<string, string> { ["message"] = "This is a test webhook delivery" }),
            ApiJsonContext.Default.WebhookEventPayload);

        var delivery = new WebhookDelivery(
            DeliveryId: Guid.NewGuid().ToString("N"),
            TenantId: tenantId,
            SubscriptionId: sub.SubscriptionId,
            EventType: WebhookEventTypes.WebhookTest,
            Payload: testPayload,
            Status: WebhookDeliveryStatus.Pending,
            Attempts: 0,
            MaxAttempts: 1,
            NextRetryAt: now,
            LastResponseCode: null,
            LastError: null,
            CreatedAt: now,
            DeliveredAt: null);

        await deliveryStore.SaveAsync(delivery, ct);

        // Save to store — the poll loop will pick it up within 30s.
        return Results.Ok(new MessageResponse($"Test event queued as delivery {delivery.DeliveryId}"));
    }

    private static async Task<IResult> ListDeliveries(
        string id,
        HttpContext context,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromServices] IWebhookSubscriptionStore subStore,
        [FromServices] IWebhookDeliveryStore deliveryStore,
        CancellationToken ct)
    {
        var sub = await subStore.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var p = page > 0 ? page : 1;
        var ps = pageSize > 0 ? Math.Min(pageSize, 100) : 20;

        var result = await deliveryStore.ListBySubscriptionAsync(id, p, ps, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> RotateSecret(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var updated = sub with
        {
            Secret = WebhookSignatureService.GenerateSecret(),
            UpdatedAt = clock.UtcNow,
        };

        await store.SaveAsync(updated, ct);

        // Return with new secret visible after rotation
        return Results.Ok(updated);
    }

    private static async Task<IResult> ResetCircuit(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        var reset = sub with
        {
            CircuitStatus = CircuitStatus.Closed,
            CircuitFailures = 0,
            CircuitOpenedAt = null,
            CircuitNextProbeAt = null,
            CircuitProbeAttempts = 0,
            UpdatedAt = clock.UtcNow,
        };

        await store.SaveAsync(reset, ct);
        return Results.Ok(new MessageResponse($"Circuit breaker reset for subscription {id}"));
    }

    private static async Task<IResult> GetCircuitStatus(
        string id,
        HttpContext context,
        [FromServices] IWebhookSubscriptionStore store,
        CancellationToken ct)
    {
        var sub = await store.GetByIdAsync(id, ct);
        if (sub is null) return Results.NotFound();

        var tenantId = GetTenantId(context);
        if (!string.Equals(sub.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Ok(new CircuitStatusResponse(
            sub.SubscriptionId,
            sub.CircuitStatus.ToString(),
            sub.CircuitFailures,
            sub.CircuitOpenedAt,
            sub.CircuitNextProbeAt,
            sub.CircuitProbeAttempts));
    }

    private static WebhookSubscription MaskSecret(WebhookSubscription sub)
        => sub with { Secret = $"{sub.Secret[..8]}...{sub.Secret[^4..]}" };

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ────────────────────────────────────────────────────────────

internal sealed record CreateWebhookSubscriptionRequest(
    string Name,
    string EndpointUrl,
    IReadOnlyList<string> EventTypes);

internal sealed record UpdateWebhookSubscriptionRequest(
    string? Name,
    string? EndpointUrl,
    IReadOnlyList<string>? EventTypes,
    bool? IsActive);

internal sealed record CircuitStatusResponse(
    string SubscriptionId,
    string Status,
    int Failures,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? NextProbeAt,
    int ProbeAttempts);
