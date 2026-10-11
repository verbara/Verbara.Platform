namespace Verbara.Platform.Channels.WhatsApp;

/// <summary>
/// Validates the configured Graph API base address (<see cref="ConfigurationKey"/>, operator decision DQ13).
/// The host reads the key explicitly and passes it here — no reflection options binding, so the value is
/// AOT-safe. The tenant's bearer token travels to this address on every send and connection test, so it
/// must be <c>https</c>; plain <c>http</c> is accepted only for a loopback host (a stub on the same machine,
/// where the token never crosses a network). A lab stub reached by a compose service name is not loopback
/// and keeps its https listener.
/// </summary>
public static class WhatsAppBaseUrl
{
    /// <summary>The configuration key the host reads the base address from.</summary>
    public const string ConfigurationKey = "WhatsApp:BaseUrl";

    /// <summary>The address used when <see cref="ConfigurationKey"/> is absent or blank.</summary>
    public const string Default = "https://graph.facebook.com";

    /// <summary>
    /// Returns the normalized base address for <paramref name="configured"/>: <see cref="Default"/> when it
    /// is null or blank, otherwise the value with surrounding whitespace and trailing <c>/</c> removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not an absolute <c>https</c> URL (or <c>http</c> to a loopback host), or it carries a
    /// query, a fragment or user information. The message names <see cref="ConfigurationKey"/> and never
    /// repeats the value, which could hold credentials.
    /// </exception>
    public static string Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Default;

        var value = configured.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw Invalid("it must be an absolute URL");

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var isLoopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
            throw Invalid("it must use https (plain http is accepted only for a loopback host)");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw Invalid("it must not carry user information");

        if (!string.IsNullOrEmpty(uri.Query) || value.Contains('?', StringComparison.Ordinal))
            throw Invalid("it must not carry a query");

        if (!string.IsNullOrEmpty(uri.Fragment) || value.Contains('#', StringComparison.Ordinal))
            throw Invalid("it must not carry a fragment");

        return value.TrimEnd('/');
    }

    private static InvalidOperationException Invalid(string reason) =>
        new($"The configuration value '{ConfigurationKey}' is invalid: {reason}.");
}
