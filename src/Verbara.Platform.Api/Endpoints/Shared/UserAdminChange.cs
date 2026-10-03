using System.Globalization;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>The services <see cref="UserAdminChange.ApplyAsync"/> acts through.</summary>
internal sealed record UserAdminChangeServices(
    SessionService Sessions,
    IAuditService Audit,
    PlatformEventBus EventBus,
    ITenantRoleStore TenantRoles,
    IRoleTemplateStore RoleTemplates,
    IUserRoleStore UserRoles,
    PermissionResolver Permissions,
    ILogger Logger);

/// <summary>
/// What a change an administrator made to a user's role or status entails besides the write itself,
/// applied after it is stored (so a client reconnecting after its connection is cut already meets the
/// new values) and from the values the store replaced (so it follows the transition that was actually
/// written, not the one the request expected).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A role change moves the user's RBAC roles, the only source of server-side permissions, from
/// the ones the former role grants to the one the new role grants (<see cref="DefaultTenantRole.MoveAsync"/>),
/// and drops the user's cached permissions on every node. A demoted administrator keeps none of the
/// former role's permissions; custom roles stay.</item>
/// <item>Leaving Active, or moving to a lower role (Admin &gt; Supervisor &gt; Agent &gt; Api), revokes the
/// refresh-token lineage, once, so no further access token is minted from it: the next one comes from a
/// sign-in, with the role as stored. Only leaving Active also cuts live Realtime and SSE connections
/// (<see cref="UserAccessRevokedEvent"/>): after a role change they would reconnect with the same token.</item>
/// <item>User-bound API keys are not revoked: they stop authenticating through the status check on every
/// request, and work again if the account is re-activated.</item>
/// <item>Access tokens already issued keep the role they were issued with until they expire (at most
/// 15 minutes); there is no per-request lookup for them. Impersonation tokens are checked on every
/// request against their impersonator's status and role (<see cref="AccountStatusGate"/>).</item>
/// <item>Every status change and every role change is audited (<c>user.status_changed</c>,
/// <c>user.role_changed</c>), with the impersonation context when the caller is impersonating.</item>
/// </list>
/// </remarks>
internal static partial class UserAdminChange
{
    public static async Task ApplyAsync(
        HttpContext context, AdminFields previous, User user, UserAdminChangeServices services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(services);

        var statusChanged = previous.Status != user.Status;
        var roleChanged = previous.Role != user.Role;
        if (!statusChanged && !roleChanged)
            return;

        var roleMove = roleChanged ? await MoveRbacRolesAsync(user, previous.Role, services, ct) : null;

        var actorId = CallerIdentity.ResolveUserIdOrSystem(context.User);
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var leavesAccess = statusChanged && !user.CanAuthenticate;
        var downgrade = roleChanged && Rank(user.Role) < Rank(previous.Role);

        var revokedSessions = 0;
        if (leavesAccess || downgrade)
        {
            revokedSessions = await services.Sessions.RevokeAllSessionsForUserAsync(
                user.TenantId.Value, actorId, user.UserId.Value,
                ip, context.Request.Headers.UserAgent.FirstOrDefault(), ct);
        }

        if (leavesAccess)
        {
            services.EventBus.Publish(new UserAccessRevokedEvent(
                user.TenantId.Value, user.UserId.Value, AccountStatusGate.StatusName(user.Status)));
        }

        if (statusChanged)
        {
            var metadata = BaseMetadata(context, ip, revokedSessions);
            metadata["old_status"] = previous.Status.ToString();
            metadata["new_status"] = user.Status.ToString();
            await RecordAsync(context, user, services.Audit, "user.status_changed", leavesAccess ? "warning" : "info", actorId, metadata, ct);
        }

        if (roleChanged)
        {
            var metadata = BaseMetadata(context, ip, revokedSessions);
            metadata["old_role"] = previous.Role.ToString();
            metadata["new_role"] = user.Role.ToString();
            if (roleMove is { } move)
            {
                metadata["rbac_roles_removed"] = string.Join(',', move.Removed);
                metadata["rbac_role_granted"] = move.Granted ?? "";
            }
            else
            {
                metadata["rbac_roles_moved"] = "false";
            }

            // A role change changes what the user may do, in either direction.
            await RecordAsync(context, user, services.Audit, "user.role_changed", "warning", actorId, metadata, ct);
        }
    }

    /// <summary>
    /// The rank of a role: a change to a lower one is a downgrade. <see cref="UserRole.Api"/> is a
    /// machine role and ranks lowest; a role this code does not know ranks lowest too, so a change to
    /// it ends the lineage.
    /// </summary>
    internal static int Rank(UserRole role) => role switch
    {
        UserRole.Admin => 3,
        UserRole.Supervisor => 2,
        UserRole.Agent => 1,
        _ => 0,
    };

    // The user's RBAC roles move with the role, and the permissions cached for it are dropped on this
    // node and published to the others whatever the outcome. A failed move leaves the role change in
    // place and is logged as an error: the request is not failed for it, because the role is already
    // written and a retry would find nothing to change.
    private static async Task<DefaultRoleMove?> MoveRbacRolesAsync(
        User user, UserRole previousRole, UserAdminChangeServices services, CancellationToken ct)
    {
        try
        {
            var move = await DefaultTenantRole.MoveAsync(
                services.TenantRoles, services.RoleTemplates, services.UserRoles,
                user.TenantId, user.UserId, previousRole, user.Role, ct);
            if (move.Granted is null)
                LogNoRbacRoleForRole(services.Logger, user.UserId.Value, user.TenantId.Value, user.Role);
            return move;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRbacMoveFailed(services.Logger, ex, user.UserId.Value, user.TenantId.Value, previousRole, user.Role);
            return null;
        }
        finally
        {
            services.Permissions.InvalidateUser(user.TenantId, user.UserId);
        }
    }

    private static Dictionary<string, string> BaseMetadata(HttpContext context, string? ip, int revokedSessions)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["revoked_sessions"] = revokedSessions.ToString(CultureInfo.InvariantCulture),
            ["ip"] = ip ?? "unknown",
            ["endpoint"] = context.Request.Path.Value ?? "",
        };
        CallerIdentity.AddImpersonationContext(metadata, context.User);
        return metadata;
    }

    private static Task RecordAsync(
        HttpContext context, User user, IAuditService audit, string action, string severity, string actorId,
        Dictionary<string, string> metadata, CancellationToken ct) =>
        audit.RecordAsync(
            user.TenantId,
            category: "auth",
            action: action,
            severity: severity,
            actorId: actorId,
            actorType: "user",
            targetId: user.UserId.Value,
            targetType: "User",
            metadata: metadata,
            ct: ct);

    [LoggerMessage(EventId = 7502, Level = LogLevel.Error,
        Message = "User {UserId} in tenant {TenantId} changed role from {PreviousRole} to {Role}, but its RBAC roles could not be moved: it holds the RBAC roles it held before until they are moved.")]
    private static partial void LogRbacMoveFailed(
        ILogger logger, Exception exception, string userId, string tenantId, UserRole previousRole, UserRole role);

    [LoggerMessage(EventId = 7503, Level = LogLevel.Warning,
        Message = "User {UserId} in tenant {TenantId} holds no RBAC role for its new role {Role}: the tenant has no role for it and its role template is unknown. The role migration at the next start grants it.")]
    private static partial void LogNoRbacRoleForRole(ILogger logger, string userId, string tenantId, UserRole role);
}
