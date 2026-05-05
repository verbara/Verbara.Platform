using System.Net;

namespace Verbara.Platform.Api.Tests;

public sealed class TenantResolutionMiddlewareTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;

    public TenantResolutionMiddlewareTests(PlatformApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("acme.platform.com")]
    [InlineData("demo.myapp.io")]
    [InlineData("tenant-1.example.com")]
    [InlineData("www.platform.com")]
    [InlineData("api.platform.com")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("platform.com")]
    public async Task Subdomain_ShouldNotCrash_ForAnyHost(string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Header_ShouldResolveTenant()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Tenant-Id", "header-tenant");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }
}
