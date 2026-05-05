using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>A respondent's answer to a single survey question.</summary>
public sealed record SurveyAnswer(EntityId QuestionId, string Value);
