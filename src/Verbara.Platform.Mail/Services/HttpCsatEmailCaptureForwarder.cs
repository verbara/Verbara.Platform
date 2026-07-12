using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Mail.Services;

/// <summary>
/// The internal CSAT email capture wire shape (csat-runner Phase C). Mirrors the Api's
/// <c>CsatResponseRequest</c> field-for-field (frozen <c>fixtures/csat-response-capture.v1.json</c>)
/// so the parsed email reply POSTs the exact contract <c>POST /api/v1/csat/responses/email</c>
/// expects. Serialized via the Mail source-gen context (AOT-safe).
/// </summary>
internal sealed record CsatEmailCapturePayload(
    string ResponseToken,
    string SurveyId,
    string QuestionId,
    string Channel,
    string QueueName,
    int Rating,
    string? Comment,
    DateTimeOffset CapturedAt,
    string ConversationId);

/// <summary>
/// Configuration for <see cref="HttpCsatEmailCaptureForwarder"/> — the internal API base address and
/// the shared service key that authorizes the internal <c>/csat/responses/email</c> endpoint.
/// </summary>
public sealed class CsatCaptureForwardOptions
{
    /// <summary>Base address of the Platform Api host exposing the internal capture endpoint.</summary>
    public required string ApiBaseAddress { get; set; }

    /// <summary>The <c>X-Service-Key</c> shared secret the internal endpoint validates.</summary>
    public required string ServiceKey { get; set; }
}

/// <summary>
/// Forwards a parsed CSAT email reply to the internal capture endpoint over HTTP (csat-runner
/// Phase C). Named-client based so the resilience + base-address wiring stays in the composition
/// root. The question id is always <c>SurveyQuestionIds.CsatRating</c> ("csat-rating-v1") to match
/// the endpoint's validation; the reply-token field carries the empty string because the internal
/// endpoint is service-key gated and does not re-verify the reply token.
/// </summary>
internal sealed class HttpCsatEmailCaptureForwarder : ICsatEmailCaptureForwarder
{
    public const string HttpClientName = "CsatInternalCapture";

    // Matches Verbara.Platform.Surveys.SurveyQuestionIds.CsatRating (no project ref from Mail).
    private const string CsatRatingQuestionId = "csat-rating-v1";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CsatCaptureForwardOptions _options;

    public HttpCsatEmailCaptureForwarder(
        IHttpClientFactory httpClientFactory,
        IOptions<CsatCaptureForwardOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task ForwardEmailRatingAsync(CsatEmailDispatch dispatch, int rating, DateTimeOffset capturedAt, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var payload = new CsatEmailCapturePayload(
            ResponseToken: string.Empty,
            SurveyId: dispatch.SurveyId,
            QuestionId: CsatRatingQuestionId,
            Channel: "email",
            QueueName: dispatch.QueueName,
            Rating: rating,
            Comment: null,
            CapturedAt: capturedAt,
            ConversationId: dispatch.ConversationId);

        var baseAddress = _options.ApiBaseAddress.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseAddress}/api/v1/csat/responses/email")
        {
            Content = JsonContent.Create(payload, MailJsonContext.Default.CsatEmailCapturePayload),
        };
        request.Headers.Add("X-Service-Key", _options.ServiceKey);
        request.Headers.Add("X-Tenant-Id", dispatch.TenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
