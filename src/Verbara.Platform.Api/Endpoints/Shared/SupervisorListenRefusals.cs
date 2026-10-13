using System.Text.Json.Serialization;

namespace Verbara.Platform.Api.Endpoints.Shared;

// supervisor-silent-listen (design D7): typed refusal bodies of the silent-listen routes, in Platform's ErrorResponse
// convention — `error` carries only a kebab-case machine code, the other members are typed fields. Frozen field for
// field in the change's fixtures (supervisor-listen-refusals.json, supervisor-listen-phone.v1.json); registered in
// ApiJsonContext. Nullable members are always written, never omitted.

/// <summary>
/// A refusal of a silent-listen route (start, stop, current or listen-endpoint). <see cref="ConversationId"/> is the
/// route's conversation id (null on the routes that have none), except <see cref="AlreadyListening"/>, where it and
/// <see cref="ListenId"/> name the caller's existing listen.
/// </summary>
internal sealed record SupervisorListenRefusal(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConversationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ListenId)
{
    /// <summary>403: the caller does not hold <c>contacts:conversation:monitor</c>.</summary>
    public const string NotPermitted = "not-permitted";

    /// <summary>400: <c>destination</c> is neither <c>endpoint</c> nor <c>phone</c>.</summary>
    public const string InvalidDestination = "invalid-destination";

    /// <summary>503: telephony (AMI) is not configured on this host.</summary>
    public const string VoiceUnavailable = "voice-unavailable";

    /// <summary>503, retryable: this replica does not hold the voice AMI lease.</summary>
    public const string NotLeader = "not-leader";

    /// <summary>404: no conversation with that id in the caller's tenant.</summary>
    public const string ConversationNotFound = "conversation-not-found";

    /// <summary>409: the conversation is not a live voice call with a live agent leg.</summary>
    public const string NoVoiceCall = "no-voice-call";

    /// <summary>409: the caller already has an active listen, named by the body.</summary>
    public const string AlreadyListening = "already-listening";

    /// <summary>409: the caller has no usable listen endpoint.</summary>
    public const string NoSupervisorEndpoint = "no-supervisor-endpoint";

    /// <summary>409: the caller has no stored listen phone.</summary>
    public const string NoListenPhone = "no-listen-phone";

    /// <summary>409: no outbound trunk resolves for the listen phone.</summary>
    public const string NoTrunk = "no-trunk";

    /// <summary>502: Asterisk refused or did not acknowledge the originate.</summary>
    public const string OriginateFailed = "originate-failed";

    /// <summary>404: the caller has no active listen (on that conversation, for stop).</summary>
    public const string ListenNotFound = "listen-not-found";

    public static SupervisorListenRefusal For(string code, string? conversationId, string? listenId = null) =>
        new(code, conversationId, listenId);

    /// <summary>The frozen HTTP status of <paramref name="code"/>.</summary>
    public static int StatusFor(string code) => code switch
    {
        NotPermitted => StatusCodes.Status403Forbidden,
        InvalidDestination => StatusCodes.Status400BadRequest,
        VoiceUnavailable or NotLeader => StatusCodes.Status503ServiceUnavailable,
        ConversationNotFound or ListenNotFound => StatusCodes.Status404NotFound,
        NoVoiceCall or AlreadyListening or NoSupervisorEndpoint or NoListenPhone or NoTrunk => StatusCodes.Status409Conflict,
        OriginateFailed => StatusCodes.Status502BadGateway,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a silent-listen refusal code."),
    };
}

/// <summary>400 <c>listen-phone-invalid</c>: the listen phone is not strict E.164; <see cref="PhoneNumber"/> echoes the value received.</summary>
internal sealed record ListenPhoneRefusal(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PhoneNumber)
{
    public const string Code = "listen-phone-invalid";

    public static ListenPhoneRefusal For(string? phoneNumber) => new(Code, phoneNumber);
}
