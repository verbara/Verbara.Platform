using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>Who is calling an Agent Assist supervisor route.</summary>
internal static class AgentAssistCaller
{
    /// <summary>
    /// The caller's user id: the JWT <c>sub</c>, then the <c>user_id</c> claim (a user-bound API key), then
    /// <see cref="ClaimTypes.NameIdentifier"/>. <see langword="null"/> when none is present; never invents one.
    /// </summary>
    internal static string? CallerUserIdOf(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return NonEmpty(user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value)
            ?? NonEmpty(user.FindFirst("user_id")?.Value)
            ?? NonEmpty(user.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
