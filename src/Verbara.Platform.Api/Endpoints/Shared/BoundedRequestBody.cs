namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Reads a request body into memory only up to a cap, so an anonymous route cannot be made to buffer
/// an arbitrarily large body (Kestrel's own default is 30 MB, and the gateway allows more).
/// </summary>
internal static class BoundedRequestBody
{
    /// <summary>The cap on a provider-webhook delivery body: 1 MB.</summary>
    internal const int WebhookMaxBytes = 1024 * 1024;

    /// <summary>
    /// Returns the body, or <see langword="null"/> when it is larger than <paramref name="maxBytes"/>:
    /// refused up front from <c>Content-Length</c>, otherwise as soon as the read passes the cap.
    /// </summary>
    internal static async Task<ReadOnlyMemory<byte>?> ReadAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
            return null;

        using var buffer = new MemoryStream(request.ContentLength is { } length ? (int)length : 0);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return new ReadOnlyMemory<byte>(buffer.ToArray());
    }
}
