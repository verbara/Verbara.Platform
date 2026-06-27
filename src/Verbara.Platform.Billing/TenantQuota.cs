using Verbara.Platform.Core;

namespace Verbara.Platform.Billing;

/// <summary>
/// Enforced resource limits for a tenant.
/// </summary>
public sealed class TenantQuota : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public int MaxConcurrentChannels { get; set; } = 100;
    public int MaxActiveCampaigns { get; set; } = 10;
    public long? MaxMonthlyVoiceMinutes { get; set; }
    public long? MaxMonthlyMessages { get; set; }
    public long? MaxStorageBytes { get; set; }
    public int? MaxActiveAgents { get; set; }
    /// <summary>Monthly platform-LLM allowance in <b>AI Credits</b> (1 credit = PlatformLlmOptions.CreditTokenRatio tokens). Null = unlimited / pay-as-you-go.</summary>
    public long? AiCreditsMonthly { get; set; }
    public QuotaAction QuotaAction { get; set; } = QuotaAction.Warn;
}

/// <summary>
/// What happens when a quota limit is reached.
/// </summary>
public enum QuotaAction
{
    Warn,
    SoftBlock,
    HardBlock,
}

/// <summary>
/// Structured outcome of a quota check — the single value the classify endpoint switches on (dropping the
/// brittle second <c>GetQuotaStatusAsync</c> re-read). The enforcement service is the sole authority. Under
/// the AI-credit ledger (ADR-0033), an exhausted <b>prepaid</b> balance maps to <see cref="SoftBlock"/> /
/// <see cref="HardBlock"/> only for tenants whose <see cref="QuotaAction"/> is <c>SoftBlock</c>/<c>HardBlock</c>;
/// a <c>Warn</c> tenant is never hard-blocked at zero — it surfaces <see cref="Warn"/> (Allowed) and overflows
/// into the billable <c>PostPaid</c> tail (preserving #4's postpaid overage).
/// </summary>
public enum QuotaOutcome
{
    /// <summary>The usage is permitted; serve normally.</summary>
    Allow = 0,

    /// <summary>The limit is reached but the tenant keeps serving — the excess overflows into billable overage.</summary>
    Warn,

    /// <summary>The limit is reached and the tenant degrades gracefully (e.g. the empty AI suggestion).</summary>
    SoftBlock,

    /// <summary>The limit is reached and the request is hard-blocked (e.g. HTTP 402).</summary>
    HardBlock,
}

/// <summary>
/// Result of a quota check for a specific usage type. The 4th positional <see cref="Outcome"/> (default
/// <see cref="QuotaOutcome.Allow"/>) is the structured decision the endpoint switches on; the boolean
/// <see cref="Allowed"/> and <see cref="Reason"/> remain for the legacy generic path.
/// </summary>
public sealed record QuotaCheckResult(bool Allowed, string? Reason, double UsagePercent, QuotaOutcome Outcome = QuotaOutcome.Allow);
