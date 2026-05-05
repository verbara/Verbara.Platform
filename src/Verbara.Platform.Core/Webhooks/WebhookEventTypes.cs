namespace Verbara.Platform.Core.Webhooks;

/// <summary>
/// Registry of all supported webhook event type strings.
/// Values must match PlatformEvent.Type on concrete event records.
/// </summary>
public static class WebhookEventTypes
{
    public const string ConversationAssigned = "conversation.assigned";
    public const string ConversationMessage = "conversation.message";
    public const string ConversationStateChanged = "conversation.state_changed";
    public const string AgentStateChanged = "agent.state_changed";
    public const string CampaignStatusChanged = "campaign.status_changed";
    public const string CampaignMetricsUpdated = "campaign.metrics_updated";
    public const string CampaignDispositionSubmitted = "campaign.disposition_submitted";
    public const string AgentAssistSuggestion = "agentassist.suggestion";
    public const string AgentAssistSentiment = "agentassist.sentiment";
    public const string AgentAssistComplianceAlert = "agentassist.compliance_alert";
    public const string AgentAssistTranscript = "agentassist.transcript";

    /// <summary>Synthetic event type sent by POST /{id}/test endpoint.</summary>
    public const string WebhookTest = "webhook.test";

    /// <summary>All valid event types that tenants can subscribe to.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ConversationAssigned,
        ConversationMessage,
        ConversationStateChanged,
        AgentStateChanged,
        CampaignStatusChanged,
        CampaignMetricsUpdated,
        CampaignDispositionSubmitted,
        AgentAssistSuggestion,
        AgentAssistSentiment,
        AgentAssistComplianceAlert,
        AgentAssistTranscript,
    ];

    /// <summary>
    /// Event type descriptions for the /api/webhooks/event-types endpoint.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [ConversationAssigned] = "Fired when a conversation is assigned to an agent",
        [ConversationMessage] = "Fired when a new message arrives in a conversation",
        [ConversationStateChanged] = "Fired when a conversation changes state",
        [AgentStateChanged] = "Fired when an agent's presence state changes",
        [CampaignStatusChanged] = "Fired when an outbound campaign changes status",
        [CampaignMetricsUpdated] = "Fired when campaign dialing metrics are updated",
        [CampaignDispositionSubmitted] = "Fired when an agent submits a disposition for a campaign call",
        [AgentAssistSuggestion] = "Fired when an agent assist suggestion is generated",
        [AgentAssistSentiment] = "Fired when a sentiment reading is produced for a call",
        [AgentAssistComplianceAlert] = "Fired when a compliance rule violation is detected",
        [AgentAssistTranscript] = "Fired when a transcript segment is produced during a call",
    };

    /// <summary>Returns true if the event type is a valid subscribable type.</summary>
    public static bool IsValid(string eventType) => All.Contains(eventType);
}
