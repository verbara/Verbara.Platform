namespace Asterisk.Platform.Core.Notifications;

/// <summary>
/// Static registry of all known notification types with their category, severity, and target roles.
/// </summary>
public static class NotificationTypeRegistry
{
    private static readonly Dictionary<string, NotificationTypeInfo> s_types = new(StringComparer.Ordinal)
    {
        ["billing.dunning_escalated"] = new("billing.dunning_escalated",
            NotificationCategory.Billing, NotificationSeverity.Critical,
            ["admin", "system_admin", "platform_admin", "partner_admin", "partner_billing"]),

        ["billing.quota_warning"] = new("billing.quota_warning",
            NotificationCategory.Billing, NotificationSeverity.Warning,
            ["admin", "system_admin"]),

        ["billing.quota_exceeded"] = new("billing.quota_exceeded",
            NotificationCategory.Billing, NotificationSeverity.Critical,
            ["admin", "system_admin", "platform_admin"]),

        ["billing.tenant_suspended"] = new("billing.tenant_suspended",
            NotificationCategory.Billing, NotificationSeverity.Critical,
            ["admin", "system_admin", "platform_admin", "partner_admin", "partner_billing"]),

        ["billing.tenant_created"] = new("billing.tenant_created",
            NotificationCategory.System, NotificationSeverity.Info,
            ["platform_admin", "partner_admin"]),

        ["security.account_locked"] = new("security.account_locked",
            NotificationCategory.Security, NotificationSeverity.Critical,
            ["admin", "system_admin"]),

        ["security.suspicious_login"] = new("security.suspicious_login",
            NotificationCategory.Security, NotificationSeverity.Warning,
            ["admin", "system_admin"]),

        ["security.mfa_enabled"] = new("security.mfa_enabled",
            NotificationCategory.Security, NotificationSeverity.Info,
            ["admin", "system_admin"]),

        ["security.mfa_disabled"] = new("security.mfa_disabled",
            NotificationCategory.Security, NotificationSeverity.Warning,
            ["admin", "system_admin"]),

        ["security.password_changed"] = new("security.password_changed",
            NotificationCategory.Security, NotificationSeverity.Info,
            ["admin", "system_admin"]),

        ["system.license_expiring"] = new("system.license_expiring",
            NotificationCategory.System, NotificationSeverity.Warning,
            ["admin", "system_admin", "platform_admin"]),

        ["system.webhook_circuit_open"] = new("system.webhook_circuit_open",
            NotificationCategory.System, NotificationSeverity.Critical,
            ["admin", "system_admin"]),

        ["system.report_failed"] = new("system.report_failed",
            NotificationCategory.System, NotificationSeverity.Warning,
            ["admin", "system_admin"]),

        ["operational.conversation_escalated"] = new("operational.conversation_escalated",
            NotificationCategory.Operational, NotificationSeverity.Critical,
            ["supervisor", "manager"]),

        ["operational.agent_offline"] = new("operational.agent_offline",
            NotificationCategory.Operational, NotificationSeverity.Warning,
            ["supervisor", "manager"]),

        ["gdpr.export_completed"] = new("gdpr.export_completed",
            NotificationCategory.System, NotificationSeverity.Info,
            ["admin", "system_admin"]),

        ["gdpr.purge_completed"] = new("gdpr.purge_completed",
            NotificationCategory.System, NotificationSeverity.Info,
            ["admin", "system_admin"]),
    };

    private static readonly IReadOnlyCollection<string> s_allTypes = s_types.Keys.ToArray();

    /// <summary>All registered notification type names.</summary>
    public static IReadOnlyCollection<string> AllTypes => s_allTypes;

    /// <summary>Gets the registration info for a notification type, or null if unknown.</summary>
    public static NotificationTypeInfo? Get(string type) =>
        s_types.TryGetValue(type, out var info) ? info : null;
}

/// <summary>
/// Describes a notification type: its category, severity, and which role templates should receive it.
/// </summary>
public sealed record NotificationTypeInfo(
    string Type,
    NotificationCategory Category,
    NotificationSeverity Severity,
    IReadOnlyList<string> TargetRoles);
