using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// dialer-places-calls-end-to-end (design D11 point 2): a campaign created through the API enters the
/// <c>[dialer-answered]</c> dialplan context when its call is answered, so the Api's Stasis consumer can continue the
/// answered leg into the campaign's target queue. <c>DialplanContext</c> is in no DTO, so the mapping is the only
/// place it is set.
/// </summary>
public sealed class CampaignEndpointsMappingTests
{
    // The body of fixtures/campaign-create.request.json (today's CreateCampaignRequest shape).
    private static CreateCampaignRequest Request() => new(
        Name: "Q4 renewals",
        Description: "Renewal reminders for expiring contracts",
        Mode: "progressive",
        TargetQueueName: "sales",
        TeamId: "7",
        MaxConcurrentCalls: 10,
        PowerRatio: null,
        TargetAbandonRate: null,
        Timezone: "America/Bogota",
        CampaignStart: "2026-11-02T13:00:00Z",
        CampaignEnd: "2026-11-30T23:00:00Z",
        Schedule: [new ScheduleDayDto("Monday", true, "09:00", "18:00")],
        Holidays: ["2026-11-16"],
        DncEnabled: true,
        MaxAttemptsPerContact: 3,
        RetryIntervalMinutes: 60,
        TimeBetweenAttemptsMinutes: 30,
        ComplianceNotes: "Consent collected at signup");

    [Fact]
    public void MapFromRequest_ShouldSetTheDialerAnsweredContext_WhenCreated()
    {
        var campaign = CampaignEndpoints.MapFromRequest(Request(), "acme");

        campaign.DialplanContext.Should().Be(CampaignDialplan.AnsweredContext,
            "the answered leg must enter [dialer-answered], which hands it to the Api's Stasis app with the campaign id");
        campaign.TargetQueueName.Should().Be("sales");
    }
}
