using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class UsersMeEndpoint
{
    public static void MapUsersMeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/users/me", GetCurrentUser).RequireAuthorization();
    }

    private static async Task<IResult> GetCurrentUser(
        HttpContext context,
        [FromServices] IUserStore userStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // "user_id" claim is set by ApiKeyAuthenticationHandler when the key has a linked user.
        // ClaimTypes.NameIdentifier holds the API key ID, not the user ID.
        var userIdValue = context.User.FindFirst("user_id")?.Value;
        if (userIdValue is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(tenantId, EntityId.From(userIdValue), ct);
        return user is null ? Results.NotFound() : Results.Ok(user);
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
