using Microsoft.Extensions.Options;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// The CSAT reply-token signer the mail service registers: Pro's <see cref="HmacCsatReplyTokenSigner"/>, built from
/// <c>Imap:TokenSigningSecret</c> the first time a reply token is checked rather than when the host starts (#690).
/// Only the reply mailbox (<c>Imap:Enabled=true</c>) checks tokens; the survey emails this service sends carry a
/// reply address the Api has already signed. So a deployment with the reply mailbox off starts and sends without the
/// secret, and one with it on is refused at start without it by <see cref="CsatReplyMailboxSettingsValidator"/>.
/// </summary>
internal sealed class DeferredCsatReplyTokenSigner : ICsatReplyTokenSigner
{
    private readonly Lazy<HmacCsatReplyTokenSigner> _signer;

    public DeferredCsatReplyTokenSigner(IOptions<ImapPollerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _signer = new Lazy<HmacCsatReplyTokenSigner>(() => Create(options.Value));
    }

    /// <inheritdoc />
    public string Sign(string tenantId, string conversationId, string surveyResponseId, DateTimeOffset issuedAt) =>
        _signer.Value.Sign(tenantId, conversationId, surveyResponseId, issuedAt);

    /// <inheritdoc />
    public CsatReplyToken? Verify(string token, DateTimeOffset now) => _signer.Value.Verify(token, now);

    private static HmacCsatReplyTokenSigner Create(ImapPollerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TokenSigningSecret))
        {
            throw new InvalidOperationException(
                "Imap:TokenSigningSecret (Imap__TokenSigningSecret) is required to check the replies to CSAT emails; " +
                "set it to the value of the Api's Csat:Email:TokenSigningSecret");
        }

        // Reuse Pro's HMAC-SHA256 verifier: the token binding and crypto live in Pro, Platform never re-implements
        // the HMAC (ADR-0022 boundary).
        return new HmacCsatReplyTokenSigner(options.TokenSigningSecret, options.TokenTtl);
    }
}
