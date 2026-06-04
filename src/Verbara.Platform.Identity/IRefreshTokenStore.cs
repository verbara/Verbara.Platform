namespace Verbara.Platform.Identity;

public interface IRefreshTokenStore
{
    Task SaveAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    Task<RefreshToken?> GetByTokenIdAsync(string tokenId, CancellationToken ct);
    Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(string tenantId, string userId, CancellationToken ct);
    Task<IReadOnlyList<RefreshToken>> GetActiveByTenantAsync(string tenantId, CancellationToken ct);
    Task RevokeAsync(string tokenId, DateTimeOffset revokedAt, string? replacedBy, CancellationToken ct);
    Task RevokeAllForUserAsync(string tenantId, string userId, DateTimeOffset revokedAt, CancellationToken ct);
}
