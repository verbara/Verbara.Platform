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

    // ── CSAT brownfield extension (Platform/ADR-0020, csat-runner Phase A) ──────
    // Additive nullable init-only properties mapping 1:1 onto the frozen capture
    // wire shape (fixtures/csat-response-capture.v1.json). All six are nullable so
    // pre-existing rows and non-CSAT survey consumers (NPS/Custom) load unchanged.

    /// <summary>Capture channel: <c>voice</c>, <c>webchat</c>, <c>email</c>, or <c>sms</c> (fixture <c>channel</c>). Null for non-CSAT rows.</summary>
    public string? Channel { get; init; }

    /// <summary>Originating queue name (fixture <c>queueName</c>). Null for non-CSAT rows.</summary>
    public string? QueueName { get; init; }

    /// <summary>Single-question CSAT rating in 1..5 (fixture <c>rating</c>). Null for non-CSAT rows.</summary>
    public int? Rating { get; init; }

    /// <summary>Optional free-text comment accompanying the rating (fixture <c>comment</c>).</summary>
    public string? Comment { get; init; }

    /// <summary>Timestamp the visitor submitted the rating (fixture <c>capturedAt</c>). Null for non-CSAT rows.</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>Correlated voice call id when the capture originated from a voice interaction. Null otherwise.</summary>
    public EntityId? CallId { get; init; }
}
