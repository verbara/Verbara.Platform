namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// Serialized projection of a per-tenant CSAT prompt template (csat-runner Phase B/E).
/// Mirrors the wire shape the admin template surface (<c>/api/v1/admin/csat/templates/*</c>,
/// Phase E) manages and the <c>Verbara.Sdk.Pro.CsatRunner.ICsatTemplateProvider</c>
/// resolves in-process. Webchat uses i18n strings and carries no per-tenant template.
/// </summary>
/// <param name="TemplateId">The template id, unique per <c>(tenant, template)</c>.</param>
/// <param name="Channel">The channel the prompt is for: <c>voice</c>, <c>email</c>, or <c>sms</c>.</param>
/// <param name="Locale">The template locale (BCP-47, e.g. <c>en-US</c>).</param>
/// <param name="Subject">Subject line for channels that carry one (email); null otherwise.</param>
/// <param name="Body">The prompt body — the email message, SMS text, or voice TTS prompt.</param>
internal sealed record CsatTemplateDto(
    string TemplateId,
    string Channel,
    string Locale,
    string? Subject,
    string Body);
