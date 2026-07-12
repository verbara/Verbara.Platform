namespace Verbara.Platform.Core;

/// <summary>
/// Raised when a customer-satisfaction (CSAT) response is captured and persisted
/// (csat-runner Phase B). Published via <see cref="PlatformEventBus"/> on each
/// successful capture at <c>POST /api/v1/csat/responses/{webchat,email,sms}</c>;
/// the Realtime <c>PushToHubRelay</c> routes it to the <c>supervisor:{tenantId}</c>
/// SignalR group so the supervisor CSAT surface updates live.
/// </summary>
/// <remarks>
/// Tenant-broadcast (no <c>Metadata.UserId</c> override) — every supervisor in the
/// tenant sees the recorded rating. The heavy row is hydrated by
/// <see cref="ResponseId"/> from the survey store; this envelope carries only the
/// lightweight display fields (<see cref="Rating"/>, <see cref="Channel"/>,
/// <see cref="QueueName"/>) needed to render the live card without a refetch.
/// </remarks>
public sealed record CsatResponseRecordedEvent(
    string TenantId,
    string ResponseId,
    string SurveyId,
    string ConversationId,
    string Channel,
    string QueueName,
    int Rating,
    string? Comment,
    DateTimeOffset CapturedAt)
    : PlatformEvent(TenantId, "csat.response_recorded", CapturedAt);
