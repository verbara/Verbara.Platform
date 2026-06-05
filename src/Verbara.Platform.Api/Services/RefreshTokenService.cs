using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

internal sealed class RefreshTokenService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(24);
    private readonly IRefreshTokenStore _store;
    private readonly TimeSpan _graceWindow;

    public RefreshTokenService(IRefreshTokenStore store, TimeSpan? rotationGraceWindow = null)
    {
        _store = store;
        _graceWindow = rotationGraceWindow ?? TimeSpan.FromSeconds(15);
    }

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

        var now = DateTimeOffset.UtcNow;

        // Cross-tab refresh race: a token that was just rotated (revoked + ReplacedBy set)
        // and replayed within the grace window is treated as a benign race rather than
        // genuine reuse. This happens when two browser tabs present the same current
        // refresh token within milliseconds — tab 1 rotates it, tab 2 then sees the
        // now-revoked token. Without a grace window this would family-revoke and log out
        // both tabs. Genuine reuse (hard-revoked with no ReplacedBy, or replayed after the
        // grace) still falls through to the family-revoke path below.
        if (existing.IsRevoked)
        {
            if (existing.ReplacedBy is { } replacedById &&
                existing.RevokedAt is { } revokedAt &&
                (now - revokedAt) <= _graceWindow)
            {
                var replacement = await _store.GetByTokenIdAsync(replacedById, ct);
                if (replacement is not null &&
                    !replacement.IsRevoked &&
                    replacement.ExpiresAt > now &&
                    replacement.UserId == existing.UserId &&
                    replacement.TenantId == existing.TenantId)
                {
                    // The replacement token's RAW value is unrecoverable (only its hash is
                    // stored), so we cannot hand back the replacement itself. Instead we mint
                    // a fresh token and chain the replacement to it (replacement → new), so the
                    // racing tab gets a usable token and the family stays linked.
                    var (newRaw, newTok) = await GenerateAsync(existing.UserId, existing.TenantId, ipAddress, userAgent, ct);
                    await _store.RevokeAsync(replacement.TokenId, now, replacedBy: newTok.TokenId, ct);
                    return (newRaw, newTok);
                }
            }

            // Reuse detection: revoked token replayed → revoke ALL for user
            await _store.RevokeAllForUserAsync(existing.TenantId, existing.UserId, now, ct);
            return null;
        }

        if (existing.IsExpired(now)) return null;

        var (newRawToken, newToken) = await GenerateAsync(existing.UserId, existing.TenantId, ipAddress, userAgent, ct);
        await _store.RevokeAsync(existing.TokenId, now, newToken.TokenId, ct);
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
