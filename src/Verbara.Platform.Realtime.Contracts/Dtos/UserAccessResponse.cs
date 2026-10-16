namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Response payload for <c>GET /api/v1/internal/user-access/{tenantId}/{userId}</c> on
/// Verbara.Platform.Api. Realtime asks it once per hub connection: the JWT it validates outlives a
/// suspension (of the account or of its tenant) by up to its lifetime, so only Platform.Api can say
/// whether the user may still connect.
/// </summary>
/// <param name="TenantId">The tenant requested (echoed).</param>
/// <param name="UserId">The user requested (echoed).</param>
/// <param name="Allowed">
/// Whether the user may connect right now — Platform.Api's account- and tenant-status rule, evaluated
/// there. <see langword="false"/> also for a user that does not exist: an unknown user is a
/// definite answer, which Realtime must tell apart from an unreachable endpoint.
/// </param>
public sealed record UserAccessResponse(
    string TenantId,
    string UserId,
    bool Allowed);
