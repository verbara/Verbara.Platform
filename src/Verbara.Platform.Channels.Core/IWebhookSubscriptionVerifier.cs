using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// Implemented by a registered <see cref="IWebhookHandler"/> whose provider confirms a webhook
/// subscription with a GET handshake (Meta's <c>hub.mode</c> / <c>hub.verify_token</c> /
/// <c>hub.challenge</c>). The check is per tenant: the token is compared with the one stored in that
/// tenant's channel configuration, never with a process-wide value (whatsapp-works-for-real D5).
/// </summary>
public interface IWebhookSubscriptionVerifier
{
    /// <summary>
    /// Returns the challenge to echo when the handshake is valid for <paramref name="tenantId"/>;
    /// otherwise <c>null</c> (wrong mode, wrong or missing token, or no active configuration).
    /// </summary>
    Task<string?> VerifySubscriptionAsync(
        TenantId tenantId,
        string? mode,
        string? verifyToken,
        string? challenge,
        CancellationToken ct);
}
