using System.Net;

namespace Verbara.Platform.Api.Tests.PartnerChannel;

/// <summary>
/// partner-channel-pilot-fixes P5 (licensed-agent-reporting): the Partner-aware peaks and export stay
/// PlatformAdminOnly — a Partner administrator gets 403 on both; this change adds no Partner-facing view.
/// </summary>
public sealed class PartnerLicensingAccessTests : IClassFixture<PartnerApiFactory>
{
    private readonly HttpClient _client;

    public PartnerLicensingAccessTests(PartnerApiFactory factory) => _client = factory.CreatePartnerClient();

    [Fact]
    public async Task GetPeaks_ShouldReturn403_WhenCallerIsPartnerAdmin()
    {
        var response = await _client.GetAsync("/api/v1/management/licensing/agents?from=2026-09-01&to=2026-09-30");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetExport_ShouldReturn403_WhenCallerIsPartnerAdmin()
    {
        var response = await _client.GetAsync("/api/v1/management/licensing/agents/export?from=2026-09-01&to=2026-09-30");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
