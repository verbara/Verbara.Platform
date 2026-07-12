namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// Admin upsert body for a per-tenant CSAT prompt template (csat-runner Phase E,
/// <c>PUT /api/v1/admin/csat/templates/{id}</c>). The template id is path-bound, not in the
/// body. Typed sealed record registered in <c>ApiJsonContext</c> (Native AOT, no reflection).
/// </summary>
/// <param name="Channel">The channel the prompt is for: <c>voice</c>, <c>email</c>, or <c>sms</c>.</param>
/// <param name="Locale">The template locale (BCP-47, e.g. <c>en-US</c>).</param>
/// <param name="Body">The prompt body — the email message, SMS text, or voice TTS prompt.</param>
/// <param name="Subject">Subject line for channels that carry one (email); null otherwise.</param>
/// <param name="IsDefault">
/// Whether this is a tenant default for its <c>(channel, locale)</c>. When omitted, an update
/// keeps the existing flag and a create defaults to <c>false</c>.
/// </param>
internal sealed record UpsertCsatTemplateRequest(
    string Channel,
    string Locale,
    string Body,
    string? Subject = null,
    bool? IsDefault = null);
