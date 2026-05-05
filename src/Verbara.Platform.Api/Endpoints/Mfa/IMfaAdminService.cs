using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Mfa;

/// <summary>
/// Query + mutation surface for the MFA administration view.
/// </summary>
internal interface IMfaAdminService
{
    /// <summary>List users across one or more tenants with their MFA enrollment status.</summary>
    Task<PagedResult<MfaUserSummary>> ListAsync(MfaUserListFilter filter, CancellationToken ct);

    /// <summary>
    /// Force-reset a user's MFA: clears <c>MfaSecret</c>, recovery codes, and
    /// <c>MfaEnabled</c>. Returns true if the user was found and updated.
    /// </summary>
    Task<bool> ResetMfaAsync(TenantId tenantId, EntityId userId, CancellationToken ct);

    /// <summary>
    /// Revoke every active session for the user (RefreshToken-based). Returns
    /// the number of sessions revoked, or -1 if the user was not found.
    /// </summary>
    Task<int> RevokeSessionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
