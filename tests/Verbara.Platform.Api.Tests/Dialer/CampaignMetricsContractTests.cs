using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions, requirement "Campaign metrics and status events name a campaign
/// by its numeric id and its status by the enum name": the metrics DTO and the two campaign SSE events, serialized
/// through <see cref="ApiJsonContext"/>, have the members and JSON types of the change's fixtures, which
/// Platform.Web's campaign monitor reads verbatim (Web #460). These pin today's shapes.
/// </summary>
public sealed class CampaignMetricsContractTests
{
    [Fact]
    public void CampaignMetricsDto_ShouldMatchTheMetricsListFixture_WhenSerialized()
    {
        var fixture = DialerContractFixtures.Load("campaign-metrics-list.response.json").AsArray();
        var dto = new CampaignMetricsDto(11, "Q4 renewals", "Active", 120, 380, 0.42, 0.02, 0, 1.5);

        var json = DialerContractFixtures.Serialize(dto, ApiJsonContext.Default.CampaignMetricsDto);

        DialerContractFixtures.AssertSameMembers(fixture[0]!.AsObject(), json);
    }

    [Fact]
    public void CampaignStatusChangedEvent_ShouldMatchTheStatusChangedFixture_WhenSerialized()
    {
        var fixture = DialerContractFixtures.Object("campaign-status-changed.event.json");
        var evt = new CampaignStatusChangedEvent("acme", 11, "Q4 renewals", "Draft", "Active");

        var json = DialerContractFixtures.Serialize(evt, ApiJsonContext.Default.CampaignStatusChangedEvent);

        DialerContractFixtures.AssertSameMembers(fixture, json);
        json["campaignId"]!.GetValue<long>().Should().Be(11);
        json["newStatus"]!.GetValue<string>().Should().Be("Active");
    }

    [Fact]
    public void CampaignMetricsUpdatedEvent_ShouldMatchTheMetricsUpdatedFixture_WhenSerialized()
    {
        var fixture = DialerContractFixtures.Object("campaign-metrics-updated.event.json");
        var evt = new CampaignMetricsUpdatedEvent("acme", 11, 120, 380, 0.42, 0.02, 0);

        var json = DialerContractFixtures.Serialize(evt, ApiJsonContext.Default.CampaignMetricsUpdatedEvent);

        DialerContractFixtures.AssertSameMembers(fixture, json);
        json["campaignId"]!.GetValue<long>().Should().Be(11);
    }
}
