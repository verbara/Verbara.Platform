namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// Serialized projection of <c>Verbara.Platform.Queues.CsatConfig</c> (the per-queue
/// CSAT solicitation config, csat-runner Phase A/B). Round-trips through the admin
/// queue-update surface; a queue whose <see cref="Enabled"/> is <c>false</c> (default)
/// is never solicited for CSAT.
/// </summary>
/// <param name="Enabled">Whether CSAT is solicited for conversations from this queue.</param>
/// <param name="PreferredChannel">Preferred capture channel (<c>voice</c>/<c>webchat</c>/<c>email</c>/<c>sms</c>); null lets the engine pick.</param>
/// <param name="PromptTemplateId">Optional per-tenant <c>csat_templates</c> template id; null falls back through the provider chain.</param>
/// <param name="SamplingRatePercent">Percentage (0..100) of eligible conversations to solicit.</param>
internal sealed record QueueCsatConfigDto(
    bool Enabled,
    string? PreferredChannel,
    string? PromptTemplateId,
    int SamplingRatePercent);
