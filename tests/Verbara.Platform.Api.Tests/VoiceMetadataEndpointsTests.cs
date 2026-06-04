using System.Net;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The test factory pod is NOT the AMI leader (hosted services are stubbed), so the endpoint must
/// return the static fallback catalog with <c>source = "fallback"</c> — exercising the degradation path.
/// </summary>
public sealed class VoiceMetadataEndpointsTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly string[] ExpectedCodecs = ["ulaw", "alaw", "g722", "opus"];

    private readonly HttpClient _admin;

    public VoiceMetadataEndpointsTests(AuthenticatedPlatformApiFactory adminFactory)
    {
        _admin = adminFactory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetCodecs_ShouldReturnFallbackCatalog_WhenNotAmiLeader()
    {
        var response = await _admin.GetAsync("/api/v1/admin/voice/codecs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["source"]!.GetValue<string>().Should().Be("fallback");
        var codecs = json["codecs"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        codecs.Should().Contain(ExpectedCodecs);
    }
}
