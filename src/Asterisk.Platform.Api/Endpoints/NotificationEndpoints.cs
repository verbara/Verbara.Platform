using Asterisk.Platform.Core.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class NotificationEndpoints
{
    internal sealed record NotificationDto(
        string NotificationId,
        string Type,
        string Category,
        string Severity,
        string Title,
        string Body,
        string? ActionUrl,
        bool IsRead,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ReadAt);

    internal sealed record UnreadCountDto(int Count);

    internal static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder group)
    {
        var notifications = group.MapGroup("/notifications")
            .RequireAuthorization("Authenticated")
            .WithTags("Notifications");

        notifications.MapGet("/", ListNotifications);
        notifications.MapGet("/unread-count", GetUnreadCount);
        notifications.MapGet("/{id}", GetNotification);
        notifications.MapPut("/{id}/read", MarkRead);
        notifications.MapPut("/read-all", MarkAllRead);

        return group;
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> ListNotifications(
        HttpContext context,
        [FromQuery] bool? unreadOnly,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null)
            return Results.Forbid();

        var items = await store.ListAsync(tenantId, userId, unreadOnly, limit ?? 50, offset ?? 0, ct);
        return Results.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetUnreadCount(
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null)
            return Results.Forbid();

        var count = await store.CountUnreadAsync(tenantId, userId, ct);
        return Results.Ok(new UnreadCountDto(count));
    }

    private static async Task<IResult> GetNotification(
        string id,
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null)
            return Results.Forbid();

        var notification = await store.GetAsync(id, ct);
        if (notification is null || notification.TenantId != tenantId || notification.UserId != userId)
            return Results.NotFound();

        return Results.Ok(ToDto(notification));
    }

    private static async Task<IResult> MarkRead(
        string id,
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null)
            return Results.Forbid();

        var notification = await store.GetAsync(id, ct);
        if (notification is null || notification.TenantId != tenantId || notification.UserId != userId)
            return Results.NotFound();

        await store.MarkReadAsync(id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkAllRead(
        HttpContext context,
        [FromServices] INotificationStore store,
        CancellationToken ct)
    {
        var (tenantId, userId) = ExtractClaims(context);
        if (tenantId is null || userId is null)
            return Results.Forbid();

        await store.MarkAllReadAsync(tenantId, userId, ct);
        return Results.NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string? tenantId, string? userId) ExtractClaims(HttpContext context)
    {
        var tenantId = context.User.FindFirst("tid")?.Value ?? context.User.FindFirst("tenant_id")?.Value;
        var userId = context.User.FindFirst("sub")?.Value ?? context.User.FindFirst("user_id")?.Value;
        return (tenantId, userId);
    }

    private static NotificationDto ToDto(Notification n) => new(
        n.NotificationId, n.Type, n.Category.ToString(), n.Severity.ToString(),
        n.Title, n.Body, n.ActionUrl, n.IsRead, n.CreatedAt, n.ReadAt);
}
