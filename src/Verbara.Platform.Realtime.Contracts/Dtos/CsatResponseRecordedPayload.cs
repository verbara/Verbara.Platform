namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Wire payload for a recorded CSAT response (csat-runner Phase B), broadcast to the
/// <c>supervisor:{tenantId}</c> SignalR group so the supervisor CSAT surface updates
/// live. Mirrors the shape of <c>Verbara.Platform.Core.CsatResponseRecordedEvent</c>
/// that <c>PushToHubRelay</c> forwards.
/// </summary>
/// <param name="TenantId">The tenant the capture belongs to.</param>
/// <param name="ResponseId">The persisted survey-response id (hydrate the row by this).</param>
/// <param name="SurveyId">The survey the rating belongs to.</param>
/// <param name="ConversationId">The correlated conversation id.</param>
/// <param name="Channel">Capture channel: <c>webchat</c>, <c>email</c>, or <c>sms</c>.</param>
/// <param name="QueueName">Originating queue name.</param>
/// <param name="Rating">The CSAT rating (1..5).</param>
/// <param name="Comment">Optional free-text comment.</param>
/// <param name="CapturedAt">The instant the rating was submitted.</param>
public sealed record CsatResponseRecordedPayload(
    string TenantId,
    string ResponseId,
    string SurveyId,
    string ConversationId,
    string Channel,
    string QueueName,
    int Rating,
    string? Comment,
    DateTimeOffset CapturedAt);
