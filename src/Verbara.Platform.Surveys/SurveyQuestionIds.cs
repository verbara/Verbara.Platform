namespace Verbara.Platform.Surveys;

/// <summary>
/// Well-known survey question identifiers shared across the platform and the Pro
/// CSAT channel adapters. Centralizing the id here (rather than repeating a magic
/// string) lets every capture path — webchat, email, sms, voice — reference the
/// same single-question rating id, matching the frozen fixture <c>questionId</c>
/// (<c>fixtures/csat-response-capture.v1.json</c>).
/// </summary>
public static class SurveyQuestionIds
{
    /// <summary>The single-question CSAT rating question id ("csat-rating-v1").</summary>
    public const string CsatRating = "csat-rating-v1";
}
