namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// The frozen CSAT capture wire shape (csat-runner Phase B, D2). Bound verbatim by
/// <c>POST /api/v1/csat/responses/{webchat,email,sms}</c> — every field name matches
/// <c>fixtures/csat-response-capture.v1.json</c> 1:1 (verbatim-fixture-citation rule)
/// and maps onto the <c>Verbara.Platform.Surveys.SurveyResponse</c> CSAT extension +
/// <c>SurveyQuestionIds.CsatRating</c>.
/// </summary>
/// <param name="ResponseToken">
/// The channel token authorizing the capture. For <c>webchat</c> this is the
/// Platform-minted, session-signed <c>v1.{payload}.{sig}</c> token whose signed
/// tenant/queue/channel MUST match the submitted <see cref="QueueName"/> and
/// <see cref="Channel"/>; missing, malformed, expired, or mismatched tokens are rejected.
/// </param>
/// <param name="SurveyId">The survey this rating belongs to (fixture <c>surveyId</c>).</param>
/// <param name="QuestionId">The single-question CSAT rating id — <c>SurveyQuestionIds.CsatRating</c> (fixture <c>questionId</c>).</param>
/// <param name="Channel">Capture channel: <c>webchat</c>, <c>email</c>, or <c>sms</c> (fixture <c>channel</c>).</param>
/// <param name="QueueName">Originating queue name (fixture <c>queueName</c>).</param>
/// <param name="Rating">The CSAT rating in 1..5 (fixture <c>rating</c>); values outside 1..5 are rejected.</param>
/// <param name="Comment">Optional free-text comment (fixture <c>comment</c>).</param>
/// <param name="CapturedAt">The instant the visitor submitted the rating (fixture <c>capturedAt</c>).</param>
/// <param name="ConversationId">The correlated conversation id (fixture <c>conversationId</c>).</param>
internal sealed record CsatResponseRequest(
    string ResponseToken,
    string SurveyId,
    string QuestionId,
    string Channel,
    string QueueName,
    int Rating,
    string? Comment,
    DateTimeOffset CapturedAt,
    string ConversationId);
