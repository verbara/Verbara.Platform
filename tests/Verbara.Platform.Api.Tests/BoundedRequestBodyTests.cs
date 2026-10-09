using Verbara.Platform.Api.Endpoints.Shared;
using Microsoft.AspNetCore.Http;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The bounded body read behind the webhook 1 MB cap: refused from <c>Content-Length</c> when it is
/// declared, and by counting while reading when it is not (a chunked body).
/// </summary>
public sealed class BoundedRequestBodyTests
{
    private static HttpRequest Request(int bytes, bool declareLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[bytes]);
        if (declareLength)
            context.Request.ContentLength = bytes;
        return context.Request;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_ShouldReturnNull_WhenBodyExceedsCap(bool declareLength)
    {
        var body = await BoundedRequestBody.ReadAsync(Request(BoundedRequestBody.WebhookMaxBytes + 1, declareLength),
            BoundedRequestBody.WebhookMaxBytes, CancellationToken.None);

        body.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_ShouldReturnWholeBody_WhenBodyIsExactlyTheCap(bool declareLength)
    {
        var body = await BoundedRequestBody.ReadAsync(Request(BoundedRequestBody.WebhookMaxBytes, declareLength),
            BoundedRequestBody.WebhookMaxBytes, CancellationToken.None);

        body.Should().NotBeNull();
        body!.Value.Length.Should().Be(BoundedRequestBody.WebhookMaxBytes);
    }
}
