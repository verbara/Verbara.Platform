using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

/// <summary>
/// Per-queue customer-satisfaction (CSAT) solicitation configuration
/// (csat-runner Phase A). A queue whose <see cref="Enabled"/> is <c>false</c>
/// (the default) is never solicited for CSAT. Persisted via four additive
/// <c>queue_configs</c> columns (<c>csat_enabled</c>, <c>csat_channel</c>,
/// <c>csat_prompt_id</c>, <c>csat_sampling_rate</c>).
/// </summary>
/// <param name="Enabled">Whether CSAT is solicited for conversations from this queue.</param>
/// <param name="PreferredChannel">
/// Preferred capture channel (<c>voice</c>, <c>webchat</c>, <c>email</c>, or
/// <c>sms</c>); null lets the engine pick by conversation channel.
/// </param>
/// <param name="PromptTemplateId">
/// Optional per-tenant <c>csat_templates</c> template id used for the prompt;
/// null falls back through the provider chain.
/// </param>
/// <param name="SamplingRatePercent">
/// Percentage (0..100) of eligible conversations to solicit; enforced by a
/// <c>0..100</c> CHECK constraint on <c>csat_sampling_rate</c>.
/// </param>
public sealed record CsatConfig(
    bool Enabled,
    string? PreferredChannel,
    EntityId? PromptTemplateId,
    int SamplingRatePercent);
