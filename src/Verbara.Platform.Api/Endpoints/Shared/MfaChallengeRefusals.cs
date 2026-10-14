using Verbara.Platform.Api.Serialization;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// The stable refusal codes of <c>POST /auth/mfa/verify</c> for a challenge that can no longer be used, in
/// Platform's <see cref="ErrorResponse"/> convention: <c>error</c> carries only a kebab-case machine code.
/// </summary>
/// <remarks>
/// A bare <c>401</c> (no body) keeps meaning "wrong factor, the challenge is still live". The server can tell
/// that <em>this</em> request spent the challenge (it held the final attempt), but not "spent earlier" from
/// "expired", so those two share <see cref="Expired"/>. The <see cref="Expired"/> code still contains
/// "expired", which clients written against the older prose body already match.
/// </remarks>
internal static class MfaChallengeRefusals
{
    /// <summary>400: the token names no live challenge — unknown, empty, expired, already used, or spent earlier.</summary>
    public const string Expired = "mfa-challenge-expired";

    /// <summary>401: this request's factor was rejected and the challenge is now spent; sign in again.</summary>
    public const string Exhausted = "mfa-challenge-exhausted";

    public static IResult ExpiredResult() =>
        Results.Json(new ErrorResponse(Expired), ApiJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);

    public static IResult ExhaustedResult() =>
        Results.Json(new ErrorResponse(Exhausted), ApiJsonContext.Default.ErrorResponse,
            statusCode: StatusCodes.Status401Unauthorized);
}
