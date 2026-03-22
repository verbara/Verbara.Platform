using Asterisk.Platform.Core;

namespace Asterisk.Platform.Surveys;

/// <summary>A single question within a <see cref="Survey"/>.</summary>
public sealed class SurveyQuestion
{
    public required EntityId QuestionId { get; init; }
    public required string Text { get; init; }
    public required SurveyQuestionType Type { get; init; }

    /// <summary>Available choices for <see cref="SurveyQuestionType.Choice"/> questions.</summary>
    public IReadOnlyList<string>? Options { get; init; }
}
