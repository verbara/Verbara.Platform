using System.Net;
using System.Net.Http.Json;

namespace Asterisk.Platform.Api.Tests;

public sealed class AuthAdminTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public AuthAdminTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
        _anonClient = factory.CreateClient();
    }

    [Fact]
    public async Task GetConfig_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("mfaPolicy");
    }

    [Fact]
    public async Task UpdateConfig_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.PutAsJsonAsync("/api/admin/auth/config", new
        {
            passwordMinLength = 16,
            lockoutThreshold = 3
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("16");
    }

    [Fact]
    public async Task ListEvents_ShouldReturn200_WhenAuthenticated()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListSessions_ShouldReturn400_WhenNoUserId()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListSessions_ShouldReturn200_WhenUserIdProvided()
    {
        var response = await _authClient.GetAsync("/api/admin/auth/sessions?userId=test-user");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokeSession_ShouldReturn204_WhenAuthenticated()
    {
        var response = await _authClient.DeleteAsync("/api/admin/auth/sessions/some-token-id");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetConfig_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _anonClient.GetAsync("/api/admin/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
