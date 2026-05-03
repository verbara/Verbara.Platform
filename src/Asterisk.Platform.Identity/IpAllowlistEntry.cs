namespace Asterisk.Platform.Identity;

/// <summary>
/// A single CIDR entry in a tenant's IP allowlist.
/// Cidr is stored in canonical form (Postgres CIDR type normalises on insert).
/// </summary>
public sealed class IpAllowlistEntry
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Cidr { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// User ID of the actor who added the entry. Stored as TEXT (no FK to
    /// users) to match this repo's convention — users PK is composite
    /// (tenant_id, user_id) and other audit-bearing tables (refresh_tokens,
    /// role_assignments) likewise store user_id TEXT without FK.
    /// </summary>
    public string? CreatedByUserId { get; init; }
}
