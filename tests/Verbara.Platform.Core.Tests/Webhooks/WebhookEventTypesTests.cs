using Verbara.Platform.Core.Webhooks;
using FluentAssertions;

namespace Verbara.Platform.Core.Tests.Webhooks;

public class WebhookEventTypesTests
{
    [Fact]
    public void All_ShouldContain11EventTypes()
    {
        WebhookEventTypes.All.Should().HaveCount(11);
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenEventTypeIsInRegistry()
    {
        WebhookEventTypes.IsValid("conversation.assigned").Should().BeTrue();
        WebhookEventTypes.IsValid("agent.state_changed").Should().BeTrue();
        WebhookEventTypes.IsValid("agentassist.suggestion").Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenEventTypeIsNotInRegistry()
    {
        WebhookEventTypes.IsValid("nonexistent.event").Should().BeFalse();
        WebhookEventTypes.IsValid("webhook.test").Should().BeFalse(); // Test event is not subscribable
    }

    [Fact]
    public void Descriptions_ShouldHaveEntryForEveryEventType()
    {
        foreach (var eventType in WebhookEventTypes.All)
        {
            WebhookEventTypes.Descriptions.Should().ContainKey(eventType);
            WebhookEventTypes.Descriptions[eventType].Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData(WebhookEventTypes.ConversationAssigned, "conversation.assigned")]
    [InlineData(WebhookEventTypes.ConversationMessage, "conversation.message")]
    [InlineData(WebhookEventTypes.ConversationStateChanged, "conversation.state_changed")]
    [InlineData(WebhookEventTypes.AgentStateChanged, "agent.state_changed")]
    [InlineData(WebhookEventTypes.CampaignStatusChanged, "campaign.status_changed")]
    [InlineData(WebhookEventTypes.CampaignMetricsUpdated, "campaign.metrics_updated")]
    [InlineData(WebhookEventTypes.CampaignDispositionSubmitted, "campaign.disposition_submitted")]
    [InlineData(WebhookEventTypes.AgentAssistSuggestion, "agentassist.suggestion")]
    [InlineData(WebhookEventTypes.AgentAssistSentiment, "agentassist.sentiment")]
    [InlineData(WebhookEventTypes.AgentAssistComplianceAlert, "agentassist.compliance_alert")]
    [InlineData(WebhookEventTypes.AgentAssistTranscript, "agentassist.transcript")]
    public void Constants_ShouldMatchExpectedValues(string constant, string expected)
    {
        constant.Should().Be(expected);
    }
}
