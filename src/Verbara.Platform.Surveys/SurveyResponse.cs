using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>A contact's submitted response to a survey.</summary>
public sealed class SurveyResponse : ITenantScoped
{
    public required EntityId ResponseId { get; init; }
    public required EntityId SurveyId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required EntityId ContactId { get; init; }

    /// <summary>Agent who handled the conversation (optional).</summary>
    public EntityId? AgentId { get; init; }

    public required IReadOnlyList<SurveyAnswer> Answers { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
}
