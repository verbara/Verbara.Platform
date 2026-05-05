using System.Net;
using System.Net.Http.Headers;

namespace Verbara.Platform.Api.Tests;

public sealed class MediaEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public MediaEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task Upload_ShouldReturn201_WhenValidFile()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("hello world"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/pdf");
        content.Add(fileContent, "file", "test.pdf");

        var response = await _client.PostAsync("/api/media/upload", content);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Download_ShouldReturn404_WhenFileNotFound()
    {
        var response = await _client.GetAsync("/api/media/nonexistent/download");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
