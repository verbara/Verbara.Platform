using System.Globalization;
using Verbara.Platform.Api.Health;
using Verbara.Sdk.Ami;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// readiness-without-asterisk (design D5; Platform/ADR-0025 addendum 2026-10-13): the endpoint filter of the voice
/// action routes. While this pod's primary AMI connection is not <c>Connected</c>, including when the pool holds no
/// primary server, a request is refused with 503, <c>Retry-After: 30</c> and the route's own typed
/// <c>voice-unavailable</c> body, before the handler runs, so no AMI action is sent. An
/// <see cref="AmiNotConnectedException"/> raised by the handler (the connection dropped while the request was in
/// flight) gets the same answer instead of the error middleware's 500.
/// </summary>
/// <remarks>
/// The connection is read through <see cref="IVoiceConnectionStatus"/>, the seam the <c>asterisk</c> check reads, so
/// <c>/health/voice</c> and these routes never disagree. Group filters run after authentication and authorization
/// and before the handler, so during an outage a caller who does not own the call gets 503 instead of 403
/// <c>not-owner</c>: availability is answered first, and nothing about the resource is disclosed.
/// </remarks>
internal static partial class VoiceAvailabilityGateExtensions
{
    /// <summary>The error code of the refusal.</summary>
    internal const string VoiceUnavailable = "voice-unavailable";

    /// <summary>The <c>Retry-After</c> hint, in seconds: the rate limiter's fallback. The Sdk's reconnect cadence is not a contract.</summary>
    internal const int RetryAfterSeconds = 30;

    /// <summary>
    /// Adds the voice availability filter to <paramref name="group"/>. <paramref name="unavailable"/> builds the
    /// route's typed 503 body.
    /// </summary>
    public static RouteGroupBuilder RequireVoiceConnection(this RouteGroupBuilder group, Func<IResult> unavailable)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(unavailable);

        group.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            if (!httpContext.RequestServices.GetRequiredService<IVoiceConnectionStatus>().Read().IsConnected)
                return Refuse(httpContext, unavailable);

            try
            {
                return await next(context);
            }
            catch (AmiNotConnectedException ex)
            {
                LogDroppedInFlight(
                    httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(VoiceAvailabilityGateExtensions)),
                    httpContext.Request.Path.Value, ex.Message);
                return Refuse(httpContext, unavailable);
            }
        });

        return group;
    }

    private static IResult Refuse(HttpContext httpContext, Func<IResult> unavailable)
    {
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return unavailable();
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-GATE] The AMI connection dropped while {Path} was in flight; answered 503 voice-unavailable: {Reason}")]
    private static partial void LogDroppedInFlight(ILogger logger, string? path, string reason);
}
