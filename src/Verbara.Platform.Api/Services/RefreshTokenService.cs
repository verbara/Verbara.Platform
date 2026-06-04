using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

internal sealed class RefreshTokenService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(24);
    private readonly IRefreshTokenStore _store;

    public RefreshTokenService(IRefreshTokenStore store) => _store = store;

    public async Task<(string RawToken, RefreshToken StoredToken)> GenerateAsync(
        string userId, string tenantId, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var rawToken = GenerateOpaqueToken();
        var tokenHash = HashToken(rawToken);
        var now = DateTimeOffset.UtcNow;

        var token = new RefreshToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            TenantId = tenantId,
            TokenHash = tokenHash,
            ExpiresAt = now.Add(RefreshTokenLifetime),
            CreatedAt = now,
            LastActivityAt = now,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        await _store.SaveAsync(token, ct);
        return (rawToken, token);
    }

    public async Task<(string RawToken, RefreshToken StoredToken)?> RotateAsync(
        string rawToken, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var tokenHash = HashToken(rawToken);
        var existing = await _store.GetByHashAsync(tokenHash, ct);

        if (existing is null) return null;

        // Reuse detection: revoked token replayed → revoke ALL for user
        if (existing.IsRevoked)
        {
            await _store.RevokeAllForUserAsync(existing.TenantId, existing.UserId, DateTimeOffset.UtcNow, ct);
            return null;
        }

        if (existing.IsExpired(DateTimeOffset.UtcNow)) return null;

        var (newRawToken, newToken) = await GenerateAsync(existing.UserId, existing.TenantId, ipAddress, userAgent, ct);
        await _store.RevokeAsync(existing.TokenId, DateTimeOffset.UtcNow, newToken.TokenId, ct);
        return (newRawToken, newToken);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken ct)
    {
        var tokenHash = HashToken(rawToken);
        var existing = await _store.GetByHashAsync(tokenHash, ct);
        if (existing is not null && !existing.IsRevoked)
            await _store.RevokeAsync(existing.TokenId, DateTimeOffset.UtcNow, replacedBy: null, ct);
    }

    private static string GenerateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    internal static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
