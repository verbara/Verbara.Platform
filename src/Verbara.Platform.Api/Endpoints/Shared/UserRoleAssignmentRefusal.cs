namespace Verbara.Platform.Api.Endpoints.Shared;

// harden-rbac-migration-seeder (design D6): typed refusal of a role assignment, in Platform's ErrorResponse
// convention — `error` carries only a kebab-case machine code, the other members are typed fields. Registered in
// ApiJsonContext.

/// <summary>404 <c>user-not-found</c>: no user with <see cref="UserId"/> exists in the caller's tenant; nothing was assigned.</summary>
internal sealed record UserRoleAssignmentRefusal(string Error, string UserId)
{
    public const string UserNotFound = "user-not-found";

    public static UserRoleAssignmentRefusal ForUnknownUser(string userId) => new(UserNotFound, userId);
}
