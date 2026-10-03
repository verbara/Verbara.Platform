using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

/// <summary>
/// The RBAC role a user's <see cref="UserRole"/> grants by default: the tenant's copy of the role
/// template that role maps to.
/// </summary>
/// <remarks>
/// <para>
/// Server-side permission checks read only the user's RBAC roles (<c>user_roles</c>), never
/// <see cref="User.Role"/>. The role migration that runs at every start attaches each user's template
/// role, so without a grant at creation a user created since the last start holds no permission at all
/// until the next one. The user-creation paths grant it here instead, in the same request.
/// </para>
/// <para>
/// The role is resolved the way that migration resolves it: the tenant role whose id is the template
/// id, else the one named like the template (case-insensitively). That covers every shape a tenant's
/// roles come in: the migration's own copies (id = template id), the copies made when a tenant is
/// provisioned (<c>role_{template}_{tenant}</c>, named like the template), and the Admin role setup
/// creates (<c>admin-{tenant}</c>, named "Admin"). A role that merely derives from the template under
/// another name is an administrator's custom role and is not the default; matching on it would also
/// leave the user with a second template role once the migration runs. A tenant with no such role yet
/// gets the template cloned under its own id and name first, which is the copy the migration would
/// make, so both settle on the same single role.
/// </para>
/// </remarks>
public static class DefaultTenantRole
{
    /// <summary>
    /// The <c>assigned_by</c> of a grant made here: the role follows from the user's
    /// <see cref="UserRole"/>, as the boot-time migration's (<c>migration</c>) does, rather than from an
    /// administrator's choice.
    /// </summary>
    public const string AssignedBy = "default-role";

    /// <summary>The role template <paramref name="role"/> maps to — the map the boot-time role migration applies.</summary>
    public static string TemplateIdFor(UserRole role) => role switch
    {
        UserRole.Agent => "agent",
        UserRole.Supervisor => "supervisor",
        UserRole.Admin => "admin",
        UserRole.Api => "api",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No role template is defined for this role."),
    };

    /// <summary>
    /// The tenant role for <paramref name="templateId"/> among <paramref name="roles"/>: the one whose id
    /// is the template id, else the one named <paramref name="templateName"/> (case-insensitively), else
    /// <see langword="null"/>.
    /// </summary>
    public static TenantRole? Resolve(IReadOnlyList<TenantRole> roles, string templateId, string? templateName)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(templateId);

        return roles.FirstOrDefault(r => string.Equals(r.RoleId, templateId, StringComparison.Ordinal))
            ?? (templateName is null
                ? null
                : roles.FirstOrDefault(r => string.Equals(r.Name, templateName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Grants <paramref name="userId"/> the tenant role <paramref name="role"/> maps to, cloning the role
    /// template into the tenant first when the tenant has no such role.
    /// </summary>
    /// <returns>
    /// The id of the granted role, or <see langword="null"/> when the tenant has no such role and the
    /// template is unknown (nothing is granted then).
    /// </returns>
    /// <remarks>
    /// The grant changes the user's permissions; a caller granting to an existing user must also drop
    /// what it has cached for that user. A new user has nothing cached.
    /// </remarks>
    public static async Task<string?> GrantAsync(
        ITenantRoleStore tenantRoles,
        IRoleTemplateStore templates,
        IUserRoleStore userRoles,
        TenantId tenantId,
        EntityId userId,
        UserRole role,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantRoles);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(userRoles);

        var templateId = TemplateIdFor(role);
        var template = await templates.GetByIdAsync(templateId, ct).ConfigureAwait(false);
        var roleId = Resolve(await tenantRoles.ListAsync(tenantId, ct).ConfigureAwait(false), templateId, template?.Name)?.RoleId;

        if (roleId is null && template is not null)
        {
            try
            {
                await tenantRoles.CloneFromTemplateAsync(
                    tenantId, templateId, templateId, template.Name, template.Description, ct).ConfigureAwait(false);
                roleId = templateId;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Another writer cloned it first — a user created at the same moment, or the boot-time
                // migration. Grant that copy; anything else is not a conflict and is the caller's to handle.
                roleId = Resolve(await tenantRoles.ListAsync(tenantId, ct).ConfigureAwait(false), templateId, template.Name)?.RoleId;
                if (roleId is null)
                    throw;
            }
        }

        if (roleId is null)
            return null;

        await userRoles.AssignAsync(tenantId, userId, roleId, AssignedBy, ct).ConfigureAwait(false);
        return roleId;
    }
}
