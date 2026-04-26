using Asterisk.Platform.Api.Endpoints.Mfa;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class UsersMeEndpoint
{
    public static void MapUsersMeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users/me", GetCurrentUser).RequireAuthorization();
    }

    private static async Task<IResult> GetCurrentUser(
        HttpContext context,
        [FromServices] IUserStore userStore,
        [FromServices] IMfaPolicyEvaluator mfaPolicyEvaluator,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // "user_id" claim is set by ApiKeyAuthenticationHandler when the key has a linked user.
        // JWT-authenticated requests put the user id in "sub". Fall back to "sub" so the
        // endpoint works for both auth schemes.
        var userIdValue = context.User.FindFirst("user_id")?.Value
                          ?? context.User.FindFirst("sub")?.Value;
        if (userIdValue is null)
            return Results.Unauthorized();

        var user = await userStore.GetByIdAsync(tenantId, EntityId.From(userIdValue), ct);
        if (user is null)
            return Results.NotFound();

        var enforced = await mfaPolicyEvaluator.RequiresMfaAsync(
            tenantId.Value, user.Role, ct);

        // PolicySource captures whether the MFA requirement comes from the
        // tenant-level policy ("tenant") or the user's own enrollment ("user").
        // The Web hides "Disable MFA" only when source == "tenant".
        var policy = new MfaPolicyDto(
            Enforced: enforced,
            PolicySource: enforced ? "tenant" : "user");

        return Results.Ok(UserMeResponseDto.FromUser(user, policy));
    }

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}
