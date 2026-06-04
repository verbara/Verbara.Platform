using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Integration tests for the realtime endpoint-profile admin surface
/// (<c>/api/v1/admin/realtime/profiles</c>). Uses the pre-seeded Enterprise
/// authenticated client so <c>AdminOnly</c>, <c>RequireOperationalTenant</c>,
/// and <c>RequireLicenseFeature(Realtime)</c> filters all pass.
/// </summary>
public sealed class RealtimeEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _admin;

    public RealtimeEndpointsTests(AuthenticatedPlatformApiFactory adminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task CreateProfile_ShouldReturn400_WhenCodecTokenInvalid()
    {
        var body = new
        {
            name = "profile-badcodec",
            type = "agent",
            transport = (string?)null,
            codecs = "opus,ulwa",
            webrtc = (bool?)null,
            maxContacts = (int?)null,
            directMedia = (bool?)null,
            context = (string?)null,
            qualifyFrequency = (int?)null,
        };

        var response = await _admin.PostAsJsonAsync("/api/v1/admin/realtime/profiles", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var invalidCodecs = json["invalidCodecs"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        invalidCodecs.Should().ContainSingle().Which.Should().Be("ulwa");
    }
}
