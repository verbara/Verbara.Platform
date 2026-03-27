using System.Net;

namespace Asterisk.Platform.Api.Tests;

public sealed class OidcTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _authClient;
    private readonly HttpClient _anonClient;

    public OidcTests(AuthenticatedPlatformApiFactory factory)
    {
        _authClient = factory.CreateAuthenticatedClient();
        _anonClient = factory.CreateClient();
    }

    [Fact]
    public async Task OidcLogin_ShouldReturn400_WhenNoTenantId()
    {
        var response = await _anonClient.GetAsync("/api/auth/oidc/login");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("tenant_id");
    }

    [Fact]
    public async Task OidcLogin_ShouldReturn400_WhenOidcNotEnabled()
    {
        var response = await _anonClient.GetAsync("/api/auth/oidc/login?tenant_id=demo");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("not enabled");
    }

    [Fact]
    public async Task OidcCallback_ShouldReturn400_WhenNoTenantId()
    {
        var response = await _anonClient.GetAsync("/api/auth/oidc/callback");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("tenant_id");
    }

    [Fact]
    public async Task OidcLogout_ShouldReturn401_WhenUnauthenticated()
    {
        var response = await _anonClient.PostAsync("/api/auth/oidc/logout", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
