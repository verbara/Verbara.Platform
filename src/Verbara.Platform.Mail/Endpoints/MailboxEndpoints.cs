using Verbara.Platform.Mail.Services;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Mail.Endpoints;

internal static class MailboxEndpoints
{
    public static RouteGroupBuilder MapMailboxEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/messages", async (
            HttpContext context,
            [FromQuery] bool unreadOnly,
            [FromQuery] int top,
            [FromQuery] int skip,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            var effectiveTop = top > 0 ? Math.Min(top, 50) : 25;
            var effectiveSkip = Math.Max(skip, 0);

            var (messages, totalCount) = await graphService.ListMessagesAsync(token.AccessToken, unreadOnly, effectiveTop, effectiveSkip, ct);
            return Results.Ok(new MailboxListResponse(messages, totalCount));
        })
        .WithName("ListMessages")
        .WithTags("Mailbox");

        group.MapPost("/messages/send", async (
            HttpContext context,
            SendMailRequest request,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            await graphService.SendMessageAsync(token.AccessToken, request, ct);
            return Results.Accepted();
        })
        .WithName("SendMessage")
        .WithTags("Mailbox");

        group.MapPost("/messages/{id}/reply", async (
            HttpContext context,
            string id,
            ReplyMailRequest request,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            await graphService.ReplyMessageAsync(token.AccessToken, id, request, ct);
            return Results.Ok();
        })
        .WithName("ReplyMessage")
        .WithTags("Mailbox");

        group.MapPost("/messages/{id}/forward", async (
            HttpContext context,
            string id,
            ForwardMailRequest request,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            await graphService.ForwardMessageAsync(token.AccessToken, id, request, ct);
            return Results.Ok();
        })
        .WithName("ForwardMessage")
        .WithTags("Mailbox");

        group.MapDelete("/messages/{id}", async (
            HttpContext context,
            string id,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            await graphService.DeleteMessageAsync(token.AccessToken, id, ct);
            return Results.NoContent();
        })
        .WithName("DeleteMessage")
        .WithTags("Mailbox");

        group.MapPatch("/messages/{id}/read", async (
            HttpContext context,
            string id,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            await graphService.MarkAsReadAsync(token.AccessToken, id, ct);
            return Results.Ok();
        })
        .WithName("MarkAsRead")
        .WithTags("Mailbox");

        group.MapGet("/messages/{id}/attachments", async (
            HttpContext context,
            string id,
            [FromServices] GraphMailboxService graphService,
            [FromServices] TokenStore tokenStore,
            CancellationToken ct) =>
        {
            var (tenantId, userId) = ExtractClaims(context);
            if (tenantId is null || userId is null) return Results.Unauthorized();

            var token = await tokenStore.GetAsync(tenantId, userId, "microsoft", ct);
            if (token is null) return Results.Json(new { error = "Microsoft account not connected" }, statusCode: 401);

            var attachments = await graphService.GetAttachmentsAsync(token.AccessToken, id, ct);
            return Results.Ok(attachments);
        })
        .WithName("GetAttachments")
        .WithTags("Mailbox");

        return group;
    }

    private static (string? TenantId, string? UserId) ExtractClaims(HttpContext context)
    {
        var tenantId = context.User.FindFirst("tid")?.Value;
        var userId = context.User.FindFirst("sub")?.Value;
        return (tenantId, userId);
    }
}

internal sealed record MailMessageDto
{
    public required string Id { get; init; }
    public required string Subject { get; init; }
    public MailRecipientDto? From { get; init; }
    public required IReadOnlyList<MailRecipientDto> To { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public required bool IsRead { get; init; }
    public required string BodyPreview { get; init; }
    public required bool HasAttachments { get; init; }
}

internal sealed record MailRecipientDto
{
    public required string Email { get; init; }
    public string? Name { get; init; }
}

internal sealed record MailAttachmentDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ContentType { get; init; }
    public required int Size { get; init; }
}

internal sealed record SendMailRequest
{
    public required IReadOnlyList<MailRecipientDto> To { get; init; }
    public IReadOnlyList<MailRecipientDto>? Cc { get; init; }
    public string? Subject { get; init; }
    public required string Body { get; init; }
}

internal sealed record ReplyMailRequest(string Comment);

internal sealed record ForwardMailRequest(
    string Comment,
    IReadOnlyList<MailRecipientDto> To);

internal sealed record MailboxListResponse(
    IReadOnlyList<MailMessageDto> Messages,
    int TotalCount);
