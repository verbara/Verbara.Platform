using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Endpoints;

// supervisor-silent-listen (design D12): the wire records of the silent audio listen, frozen field for field in the
// change's fixtures and registered in ApiJsonContext. Nullable members are always written, never omitted.

/// <summary>
/// <c>POST /supervisor/conversations/{id}/listen</c> request. <see cref="Destination"/> is <c>endpoint</c> or
/// <c>phone</c>; the request never carries a number or a SIP address (an extra <c>phoneNumber</c> is ignored).
/// </summary>
internal sealed record SupervisorListenStartRequest(string? Destination);

/// <summary>The caller's listen: <see cref="State"/> is <c>dialing</c> until the audio connects, then <c>listening</c>.</summary>
internal sealed record SupervisorListenDto(
    string ListenId,
    string ConversationId,
    string Destination,
    string State,
    DateTimeOffset StartedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ListeningSince);

/// <summary><c>POST …/listen/stop</c> answer: the caller's listen ended by request (<see cref="EndReason"/> = <c>stopped</c>).</summary>
internal sealed record SupervisorListenStoppedDto(
    string ListenId,
    string ConversationId,
    DateTimeOffset StoppedAt,
    string EndReason);

/// <summary>
/// <c>POST /supervisor/listen-endpoint</c> answer, to the caller only: the SIP credentials of their receive-only listen
/// endpoint. The single place <see cref="SipPassword"/> crosses HTTP.
/// </summary>
internal sealed record SupervisorListenEndpointDto(
    string EndpointId,
    string AuthorizationUsername,
    string SipPassword,
    string DisplayName,
    string ListenCallerName)
{
    /// <summary>The caller ID name every listen INVITE presents.</summary>
    public const string CallerName = "Verbara Listen";
}

/// <summary><c>PUT /admin/users/{id}/listen-phone</c> request; <c>null</c> clears the number.</summary>
internal sealed record ListenPhoneRequest(string? PhoneNumber);

/// <summary>A user's listen phone, as an administrator reads or sets it.</summary>
internal sealed record ListenPhoneDto(
    string UserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PhoneNumber);
