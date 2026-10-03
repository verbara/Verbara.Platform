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
/// until the next one. The user-creation paths grant it here instead, in the same request; a change of
/// <see cref="User.Role"/> moves it (<see cref="MoveAsync"/>).
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
    /// The role templates whose tenant copies come with <paramref name="role"/>: its own template and,
    /// for <see cref="UserRole.Admin"/>, every administrator template — System Admin, Platform Admin
    /// (which setup grants the first host administrator) and Partner Admin. Leaving the role takes them
    /// all away; none of them belongs to a user who is no longer an administrator. A value that names no
    /// role comes with none.
    /// </summary>
    public static IReadOnlyList<string> TemplatesGrantedWith(UserRole role) => role switch
    {
        UserRole.Admin => s_administratorTemplates,
        _ when Enum.IsDefined(role) => [TemplateIdFor(role)],
        _ => [],
    };

    private static readonly string[] s_administratorTemplates = ["admin", "system_admin", "platform_admin", "partner_admin"];

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
    /// The ids, among <paramref name="roles"/> of <paramref name="tenantId"/>, of the tenant roles that
    /// come with <paramref name="role"/>: for each of its templates (<see cref="TemplatesGrantedWith"/>),
    /// every shape the tenant's copy of it comes in — the template id, the provisioned
    /// <c>role_{template}_{tenant}</c>, the id setup gives it (<c>admin-{tenant}</c>,
    /// <c>platform-admin-{tenant}</c>), and the template's name (<paramref name="templateNames"/>,
    /// case-insensitively). A custom role made from a template under another name is not among them.
    /// </summary>
    public static IReadOnlyList<string> RolesGrantedWith(
        IReadOnlyList<TenantRole> roles, TenantId tenantId, UserRole role, IReadOnlyDictionary<string, string> templateNames)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(templateNames);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var templateId in TemplatesGrantedWith(role))
        {
            ids.Add(templateId);
            ids.Add($"role_{templateId}_{tenantId.Value}");
            if (SetupRoleIdFor(templateId, tenantId) is { } setupRoleId)
                ids.Add(setupRoleId);
            if (templateNames.TryGetValue(templateId, out var name))
                names.Add(name);
        }

        return roles
            .Where(r => r.TenantId == tenantId && (ids.Contains(r.RoleId) || names.Contains(r.Name)))
            .Select(r => r.RoleId)
            .ToList();
    }

    // The ids setup gives the roles it creates for the first administrators (SetupEndpoints).
    private static string? SetupRoleIdFor(string templateId, TenantId tenantId) => templateId switch
    {
        "admin" => $"admin-{tenantId.Value}",
        "platform_admin" => $"platform-admin-{tenantId.Value}",
        _ => null,
    };

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

        var roleId = await ResolveOrCloneAsync(tenantRoles, templates, tenantId, role, ct).ConfigureAwait(false);
        if (roleId is null)
            return null;

        await userRoles.AssignAsync(tenantId, userId, roleId, AssignedBy, ct).ConfigureAwait(false);
        return roleId;
    }

    /// <summary>
    /// Moves <paramref name="userId"/>, whose role changed from <paramref name="previousRole"/> to
    /// <paramref name="role"/>, from the tenant roles that come with the former
    /// (<see cref="RolesGrantedWith"/>) to the one the latter maps to — cloning its template into the
    /// tenant first when the tenant has no such role — in one write of the user's roles. Roles that do
    /// not come with the former role (an administrator's custom ones, or another template's copy) are
    /// kept.
    /// </summary>
    /// <remarks>
    /// The move changes the user's permissions; the caller must drop what it has cached for that user.
    /// </remarks>
    public static async Task<DefaultRoleMove> MoveAsync(
        ITenantRoleStore tenantRoles,
        IRoleTemplateStore templates,
        IUserRoleStore userRoles,
        TenantId tenantId,
        EntityId userId,
        UserRole previousRole,
        UserRole role,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantRoles);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(userRoles);

        // A value that names no role grants nothing; the former role's roles are taken away all the same.
        var granted = Enum.IsDefined(role)
            ? await ResolveOrCloneAsync(tenantRoles, templates, tenantId, role, ct).ConfigureAwait(false)
            : null;

        var templateNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var template in await templates.GetAllAsync(ct).ConfigureAwait(false))
            templateNames[template.TemplateId] = template.Name;
        var former = RolesGrantedWith(
            await tenantRoles.ListAsync(tenantId, ct).ConfigureAwait(false), tenantId, previousRole, templateNames);

        var removed = await userRoles.MoveAsync(tenantId, userId, former, granted, AssignedBy, ct).ConfigureAwait(false);
        return new DefaultRoleMove(removed, granted);
    }

    /// <summary>
    /// The id of the tenant role <paramref name="role"/> maps to, cloning the template into the tenant
    /// first when the tenant has no such role; <see langword="null"/> when there is none and the template
    /// is unknown.
    /// </summary>
    private static async Task<string?> ResolveOrCloneAsync(
        ITenantRoleStore tenantRoles, IRoleTemplateStore templates, TenantId tenantId, UserRole role, CancellationToken ct)
    {
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

        return roleId;
    }
}

/// <summary>What <see cref="DefaultTenantRole.MoveAsync"/> did to a user's RBAC roles.</summary>
/// <param name="Removed">The roles of the former role the user held and no longer holds.</param>
/// <param name="Granted">
/// The role of the new role the user now holds, or <see langword="null"/> when the tenant has none and
/// its template is unknown (nothing was granted then).
/// </param>
public sealed record DefaultRoleMove(IReadOnlyList<string> Removed, string? Granted);
