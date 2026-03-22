using Asterisk.Platform.Core;

namespace Asterisk.Platform.Surveys;

/// <summary>Survey definition — template used to collect responses.</summary>
public sealed class Survey : ITenantScoped
{
    public required EntityId SurveyId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; init; }
    public required SurveyType Type { get; init; }
    public required IReadOnlyList<SurveyQuestion> Questions { get; init; }
    public bool IsActive { get; set; } = true;
}
