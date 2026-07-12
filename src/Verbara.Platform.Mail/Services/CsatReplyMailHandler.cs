using System.Text.RegularExpressions;
using MimeKit;
using Verbara.Platform.Core.Email;
using Verbara.Sdk.Pro.CsatRunner.Tokens;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// The correlated CSAT binding a dispatch row carries (csat-runner Phase C). The email endpoint's
/// wire shape needs <c>surveyId</c> + <c>queueName</c> that the reply token itself does not carry;
/// these are hydrated from the <c>csat_pending_dispatches</c> row (email channel, <c>correlator =
/// token</c>) that Pro's dispatcher wrote at send time.
/// </summary>
public sealed record CsatEmailDispatch(
    string TenantId,
    string SurveyId,
    string QueueName,
    string ConversationId);

/// <summary>
/// Resolves a dispatched CSAT email into its binding, either by the verified reply <c>token</c> or,
/// when a forwarder stripped the <c>+token</c> envelope suffix, by the <c>In-Reply-To</c> header
/// matched against the dispatched <c>Message-Id</c>. Implemented over
/// <c>csat_pending_dispatches</c> in the host; kept a seam so the reply handler's parsing/correlation
/// can be unit-tested without a database.
/// </summary>
public interface ICsatEmailDispatchResolver
{
    /// <summary>Resolves the dispatch bound to a verified reply <paramref name="token"/>.</summary>
    Task<CsatEmailDispatch?> ResolveByTokenAsync(string token, CancellationToken ct);

    /// <summary>Resolves the dispatch whose <c>Message-Id</c> equals <paramref name="inReplyToMessageId"/>.</summary>
    Task<CsatEmailDispatch?> ResolveByInReplyToAsync(string inReplyToMessageId, CancellationToken ct);
}

/// <summary>
/// Forwards a parsed CSAT email reply to the internal capture endpoint
/// (<c>POST /api/v1/csat/responses/email</c>, service-key gated). A seam over the HTTP call so the
/// reply handler is unit-testable without a live host; the concrete implementation lives in the host.
/// </summary>
public interface ICsatEmailCaptureForwarder
{
    /// <summary>POSTs the parsed CSAT rating for <paramref name="dispatch"/> to the internal email endpoint.</summary>
    Task ForwardEmailRatingAsync(CsatEmailDispatch dispatch, int rating, DateTimeOffset capturedAt, CancellationToken ct);
}

/// <summary>The outcome of handling an inbound reply message.</summary>
public enum CsatReplyOutcome
{
    /// <summary>A rating was parsed, correlated, and forwarded.</summary>
    Captured,

    /// <summary>The reply token was present but malformed / tampered / expired, and no In-Reply-To fallback matched.</summary>
    TokenInvalid,

    /// <summary>Correlation succeeded but the message body/subject carried no <c>1..5</c> rating.</summary>
    NoRating,

    /// <summary>Neither the token nor the In-Reply-To header correlated to an open dispatch.</summary>
    NoCorrelation,
}

/// <summary>
/// Parses an inbound CSAT reply email and forwards the extracted rating (csat-runner Phase C).
/// Correlation order: (1) verify the HMAC-signed reply token from the <c>Reply-To</c>/recipient
/// <c>csat+{token}@…</c> local part (7-day TTL) via Pro's <see cref="ICsatReplyTokenSigner"/> — the
/// HMAC is NEVER re-hand-rolled here (ADR-0022 boundary); (2) when the token is absent or does not
/// verify, fall back to the <c>In-Reply-To</c> header matched against the dispatched <c>Message-Id</c>.
/// Rating extraction uses <c>\b([1-5])\b</c> against the subject first, then the first 200 chars of
/// the plain-text body. A matched reply is forwarded to the internal email capture endpoint; when
/// auto-reply is enabled the sender gets an acknowledgement.
/// </summary>
public sealed partial class CsatReplyMailHandler
{
    private readonly ICsatReplyTokenSigner _tokenSigner;
    private readonly ICsatEmailDispatchResolver _dispatchResolver;
    private readonly ICsatEmailCaptureForwarder _forwarder;
    private readonly IEmailService? _autoReply;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CsatReplyMailHandler>? _logger;
    private readonly bool _autoReplyEnabled;

    public CsatReplyMailHandler(
        ICsatReplyTokenSigner tokenSigner,
        ICsatEmailDispatchResolver dispatchResolver,
        ICsatEmailCaptureForwarder forwarder,
        bool autoReplyEnabled = false,
        IEmailService? autoReply = null,
        TimeProvider? timeProvider = null,
        ILogger<CsatReplyMailHandler>? logger = null)
    {
        _tokenSigner = tokenSigner;
        _dispatchResolver = dispatchResolver;
        _forwarder = forwarder;
        _autoReplyEnabled = autoReplyEnabled;
        _autoReply = autoReply;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    // Rating digit 1..5 on a word boundary — spec regex, applied to subject then body head.
    [GeneratedRegex(@"\b([1-5])\b", RegexOptions.CultureInvariant)]
    private static partial Regex RatingRegex();

    // csat+{token}@domain — the token lives in the local-part after the '+' subaddress delimiter.
    [GeneratedRegex(@"^csat\+([^@]+)@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReplyToTokenRegex();

    /// <summary>
    /// Handles a single inbound reply message: correlates it (token → In-Reply-To fallback), parses
    /// the rating, forwards a match, and optionally sends an auto-reply acknowledgement.
    /// </summary>
    public async Task<CsatReplyOutcome> HandleReplyAsync(MimeMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _timeProvider.GetUtcNow();

        // (1) Token path: extract the token from the Reply-To / To local part, verify sig + TTL.
        var token = ExtractToken(message);
        CsatEmailDispatch? dispatch = null;
        var tokenPresentButInvalid = false;

        if (!string.IsNullOrEmpty(token))
        {
            var verified = _tokenSigner.Verify(token, now);
            if (verified is not null)
                dispatch = await _dispatchResolver.ResolveByTokenAsync(token, ct).ConfigureAwait(false);
            else
                tokenPresentButInvalid = true;
        }

        // (2) In-Reply-To fallback: a forwarder may have stripped the +token suffix.
        if (dispatch is null)
        {
            var inReplyTo = NormalizeMessageId(message.InReplyTo);
            if (!string.IsNullOrEmpty(inReplyTo))
                dispatch = await _dispatchResolver.ResolveByInReplyToAsync(inReplyTo, ct).ConfigureAwait(false);
        }

        if (dispatch is null)
            return tokenPresentButInvalid ? CsatReplyOutcome.TokenInvalid : CsatReplyOutcome.NoCorrelation;

        // Rating: subject first, then the first 200 chars of the plain-text body.
        var rating = ExtractRating(message);
        if (rating is null)
            return CsatReplyOutcome.NoRating;

        await _forwarder.ForwardEmailRatingAsync(dispatch, rating.Value, now, ct).ConfigureAwait(false);

        if (_autoReplyEnabled && _autoReply is not null)
            await SendAutoReplyAsync(message, dispatch, ct).ConfigureAwait(false);

        if (_logger is not null)
            LogCaptured(_logger, dispatch.ConversationId, rating.Value);
        return CsatReplyOutcome.Captured;
    }

    private static string? ExtractToken(MimeMessage message)
    {
        // Prefer Reply-To, then To — the +token subaddress rides whichever the dispatcher stamped.
        foreach (var address in EnumerateMailboxes(message.ReplyTo).Concat(EnumerateMailboxes(message.To)))
        {
            var match = ReplyToTokenRegex().Match(address);
            if (match.Success)
                return match.Groups[1].Value;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateMailboxes(InternetAddressList list)
    {
        foreach (var addr in list)
        {
            if (addr is MailboxAddress mailbox && !string.IsNullOrEmpty(mailbox.Address))
                yield return mailbox.Address;
        }
    }

    private static int? ExtractRating(MimeMessage message)
    {
        var subject = message.Subject;
        if (!string.IsNullOrEmpty(subject))
        {
            var subjectMatch = RatingRegex().Match(subject);
            if (subjectMatch.Success)
                return int.Parse(subjectMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var body = message.TextBody;
        if (!string.IsNullOrEmpty(body))
        {
            var head = body.Length > 200 ? body[..200] : body;
            var bodyMatch = RatingRegex().Match(head);
            if (bodyMatch.Success)
                return int.Parse(bodyMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string? NormalizeMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;
        // MailKit already unwraps the angle brackets; trim defensively.
        return messageId.Trim().Trim('<', '>');
    }

    private async Task SendAutoReplyAsync(MimeMessage original, CsatEmailDispatch dispatch, CancellationToken ct)
    {
        var replyTo = EnumerateMailboxes(original.From).FirstOrDefault();
        if (string.IsNullOrEmpty(replyTo))
            return;

        await _autoReply!.SendAsync(
            new EmailMessage
            {
                Subject = "Thanks for your feedback",
                TextBody = "Thank you — your rating has been recorded.",
                Recipients = [new EmailRecipient(replyTo, null)],
            },
            ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "CSAT email reply captured: conversation={ConversationId} rating={Rating}")]
    private static partial void LogCaptured(ILogger logger, string conversationId, int rating);
}
