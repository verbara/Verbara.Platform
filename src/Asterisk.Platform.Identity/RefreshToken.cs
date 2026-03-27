namespace Asterisk.Platform.Identity;

public sealed class RefreshToken
{
    public required string TokenId { get; init; }
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public required string TokenHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedBy { get; set; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);
}
