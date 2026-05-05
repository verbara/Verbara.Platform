using System.Security.Cryptography;
using System.Text;

namespace Verbara.Platform.Core.Webhooks;

/// <summary>
/// HMAC-SHA256 signature computation for outbound webhook deliveries.
/// </summary>
public static class WebhookSignatureService
{
    /// <summary>
    /// Computes an HMAC-SHA256 signature as a lowercase hex string.
    /// Format: HMAC-SHA256(secret, "{timestamp}.{body}")
    /// </summary>
    public static string ComputeSignature(string timestamp, string body, string secret)
        => Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    /// <summary>
    /// Verifies that a signature matches the expected value.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public static bool VerifySignature(string timestamp, string body, string secret, string signature)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(ComputeSignature(timestamp, body, secret)),
            Encoding.UTF8.GetBytes(signature));

    /// <summary>
    /// Generates a cryptographically random secret for new subscriptions.
    /// Returns a 32-byte hex string (64 characters).
    /// </summary>
    public static string GenerateSecret()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
