using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class JwtAuthTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public JwtAuthTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
        _anonClient = factory.CreateClient();
    }

    [Fact]
    public async Task ApiKey_ShouldStillWork_WithDualScheme()
    {
        var response = await _authClient.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InvalidJwt_ShouldReturn401()
    {
        // "eyJ" prefix triggers JWT scheme, but the token is invalid
        var client = _anonClient;
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer eyJinvalid.token.here");

        var response = await client.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoAuthHeader_ShouldReturn401()
    {
        // Use the shared factory's anonymous client (no auth headers)
        var response = await _anonClient.GetAsync("/api/conversations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
