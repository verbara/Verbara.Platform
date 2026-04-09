using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Asterisk.Platform.Channels.Sms.Providers;

/// <summary>
/// Validates Twilio X-Twilio-Signature HMAC-SHA1 signatures.
/// </summary>
public static class TwilioSignatureValidator
{
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Twilio webhook signature protocol requires HMAC-SHA1")]
    public static bool Validate(string authToken, string url, IReadOnlyDictionary<string, string> parameters, string signature)
    {
        var builder = new StringBuilder(url);

        // Twilio sorts params alphabetically by key
        foreach (var param in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(param.Key);
            builder.Append(param.Value);
        }

        var data = Encoding.UTF8.GetBytes(builder.ToString());
        var key = Encoding.UTF8.GetBytes(authToken);
        var hash = HMACSHA1.HashData(key, data);
        var computed = Convert.ToBase64String(hash);

        return string.Equals(computed, signature, StringComparison.Ordinal);
    }
}
