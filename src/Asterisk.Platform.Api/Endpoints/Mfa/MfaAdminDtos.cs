namespace Asterisk.Platform.Api.Endpoints.Mfa;

/// <summary>
/// Per-user MFA enrollment summary returned by the admin list endpoint.
/// </summary>
internal sealed record MfaUserSummary(
    string UserId,
    string Username,
    string TenantId,
    string TenantName,
    string Status,
    DateTimeOffset? EnrolledAt,
    DateTimeOffset? LastUsedAt);

/// <summary>
/// Filter applied to the MFA admin list endpoint.
/// </summary>
internal sealed record MfaUserListFilter
{
    /// <summary>One of "enrolled", "not-enrolled", or "locked". Null = all.</summary>
    public string? Status { get; init; }

    /// <summary>Optional tenant filter. Null = all tenants the caller can see.</summary>
    public string? TenantId { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// MFA policy snapshot exposed in <c>/users/me</c> so the Web can hide the
/// "Disable MFA" affordance proactively when tenant policy enforces MFA.
/// </summary>
internal sealed record MfaPolicyDto(
    bool Enforced,
    string PolicySource);
