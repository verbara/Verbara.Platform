using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Sms;

/// <summary>
/// Forwards a correlated CSAT rating to the internal capture endpoint
/// (<c>POST /api/v1/csat/responses/sms</c>, service-key gated). The SMS correlator
/// depends on this seam rather than an <see cref="System.Net.Http.HttpClient"/> directly so the
/// correlation logic (window / collision / fall-through) can be unit-tested without a live host.
/// The concrete HTTP-backed implementation lives in the Api composition root.
/// </summary>
public interface ICsatCaptureForwarder
{
    /// <summary>
    /// POSTs the parsed CSAT rating to the internal SMS capture endpoint for
    /// <paramref name="tenantId"/>, binding the survey/queue/conversation the matched
    /// <c>csat_pending_dispatches</c> row carried.
    /// </summary>
    Task ForwardSmsRatingAsync(
        TenantId tenantId,
        string surveyId,
        string queueName,
        string conversationId,
        int rating,
        DateTimeOffset capturedAt,
        CancellationToken ct);
}
