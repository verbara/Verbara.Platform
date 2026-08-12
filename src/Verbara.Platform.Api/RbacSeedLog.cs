namespace Verbara.Platform.Api;

/// <summary>
/// Source-generated log messages for the startup RBAC seed (permissions, role templates, and the
/// per-tenant role migration) run from <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// ADR-0037 — this path used to write to <c>Console</c>, so a seed that aborted partway through
/// (an uncatalogued grant raising <c>23503</c> against
/// <c>role_template_permissions_permission_id_fkey</c>) left a half-populated role model with no
/// signal in the logs. The failure is now Error-level and carries the exception, but it still does
/// not abort the boot: a transient database fault must not brick the host, and the systematic
/// cause is caught at build time by the catalog-integrity guard tests.
/// </remarks>
internal static partial class RbacSeedLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "RBAC seeder: permissions, role templates, and per-tenant role migration complete.")]
    public static partial void SeedCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "RBAC seeder FAILED — the role model may be incomplete (partially seeded templates, " +
                  "missing per-tenant roles). Startup continues; re-run after fixing the cause.")]
    public static partial void SeedFailed(ILogger logger, Exception exception);
}
