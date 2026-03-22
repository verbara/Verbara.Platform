namespace Asterisk.Platform.Surveys;

/// <summary>Aggregated score metrics for a survey.</summary>
public sealed record SurveyScoreSummary(
    int TotalResponses,
    double AverageScore,
    int? Promoters,
    int? Passives,
    int? Detractors,
    double? NpsScore);
