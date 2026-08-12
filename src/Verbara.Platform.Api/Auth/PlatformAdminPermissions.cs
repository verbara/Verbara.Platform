namespace Verbara.Platform.Api.Auth;

/// <summary>
/// The permission ids named by the <see cref="PlatformAdminRequirement"/> gates wired in
/// <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0037 — <c>PermissionResolver.HasPermission</c> is an exact set-membership test: there is no
/// alias, prefix or implication layer at check time. A gate that names an id absent from the
/// permission catalog can therefore never be satisfied on its permission, and the surface stays
/// reachable only through the <c>Admin</c>/<c>SystemAdmin</c> role shortcut in
/// <see cref="PlatformAdminAuthorizationHandler"/> — which is exactly how six admin surfaces went
/// weeks with an unsatisfiable gate.
/// </para>
/// <para>
/// Holding the ids here rather than as string literals at the policy site lets a test enumerate
/// <see cref="All"/> and assert that every gate id is catalogued.
/// </para>
/// </remarks>
public static class PlatformAdminPermissions
{
    /// <summary>
    /// Administer other users' MFA — <c>/api/v1/management/mfa</c> (list enrollment status,
    /// reset TOTP secrets, revoke sessions).
    /// </summary>
    public const string MfaManage = "system:mfa:manage";

    /// <summary>Query the audit log — <c>GET /api/v1/admin/audit/events</c>.</summary>
    public const string AuditView = "system:audit:view";

    /// <summary>
    /// Bulk-export the audit log — <c>GET /api/v1/admin/audit/export</c>. Separate from
    /// <see cref="AuditView"/> so a mass extract can be revoked while in-app viewing stays open.
    /// </summary>
    public const string AuditExport = "system:audit:export";

    /// <summary>
    /// Administer impersonation sessions — <c>/api/v1/management/impersonation/sessions</c>
    /// (list active, revoke, read history). Distinct from <c>platform:tenant:impersonate</c>,
    /// which authorises <em>starting</em> a session; <c>system_admin</c> holds this one and is
    /// deliberately excluded from the other (ADR-0037).
    /// </summary>
    public const string ImpersonationManage = "system:impersonation:manage";

    /// <summary>
    /// Read the retention overview and configuration — <c>GET /api/v1/management/retention</c>.
    /// </summary>
    public const string RetentionView = "system:retention:view";

    /// <summary>
    /// Change retention configuration (DryRun toggle) and trigger a manual run —
    /// <c>PATCH /api/v1/management/retention/config</c>, <c>POST .../run-now</c>.
    /// </summary>
    public const string RetentionManage = "system:retention:manage";

    /// <summary>
    /// Rotate the JWT signing keys — <c>POST /api/v1/management/security/jwt/rotate-key</c>.
    /// </summary>
    /// <remarks>
    /// Keeps its dot-notation spelling. It is the one dot id that IS catalogued, so it is gated,
    /// granted and functional end to end; renaming it would churn live
    /// <c>tenant_role_permissions</c> rows for no behavioural gain. Recorded as an accepted
    /// inconsistency in ADR-0037 rather than left unnoticed.
    /// </remarks>
    public const string JwtRotate = "security.jwt.rotate";

    /// <summary>
    /// Mint AI-credit top-up grants for a tenant — <c>POST /api/v1/management/credits/grants</c>.
    /// Operator-only: deliberately absent from <c>AllPermissions()</c> so the <c>admin</c> and
    /// <c>system_admin</c> templates do not inherit the cross-tenant grant.
    /// </summary>
    public const string CreditsGrant = "billing:credits:grant";

    /// <summary>
    /// Every permission id named by a <see cref="PlatformAdminRequirement"/> gate. Each MUST be a
    /// member of the permission catalog (ADR-0037).
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        MfaManage,
        AuditView,
        AuditExport,
        ImpersonationManage,
        RetentionView,
        RetentionManage,
        JwtRotate,
        CreditsGrant,
    ];
}
