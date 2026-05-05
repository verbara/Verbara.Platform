namespace Verbara.Platform.Core;

/// <summary>
/// Per-tenant data retention configuration. Null fields = indefinite retention (no auto-purge).
/// </summary>
public sealed record TenantRetentionPolicy
{
    public required string TenantId { get; init; }
    public int? ConversationRetentionDays { get; init; }
    public int? AuthEventRetentionDays { get; init; }
    public int? AuditRetentionDays { get; init; }
    public int? UsageRecordRetentionDays { get; init; }
}
