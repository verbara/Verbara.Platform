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
/// Result of a quota check for a specific usage type.
/// </summary>
public sealed record QuotaCheckResult(bool Allowed, string? Reason, double UsagePercent);
