using System.Collections.Concurrent;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    private readonly ConcurrentDictionary<string, RefreshToken> _items = new();

    public Task SaveAsync(RefreshToken token, CancellationToken ct)
    {
        _items[token.TokenId] = token;
        return Task.CompletedTask;
    }

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(t => t.TokenHash == tokenHash);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<RefreshToken>> GetActiveByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = _items.Values
            .Where(t => t.TenantId == tenantId && t.UserId == userId && t.IsActive(now))
            .ToList();
        return Task.FromResult<IReadOnlyList<RefreshToken>>(result);
    }

    public Task<IReadOnlyList<RefreshToken>> GetActiveByTenantAsync(string tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = _items.Values
            .Where(t => t.TenantId == tenantId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToList();
        return Task.FromResult<IReadOnlyList<RefreshToken>>(result);
    }

    public Task RevokeAsync(string tokenId, DateTimeOffset revokedAt, string? replacedBy, CancellationToken ct)
    {
        if (_items.TryGetValue(tokenId, out var token))
        {
            token.RevokedAt = revokedAt;
            token.ReplacedBy = replacedBy;
        }

        return Task.CompletedTask;
    }

    public Task RevokeAllForUserAsync(string tenantId, string userId, DateTimeOffset revokedAt, CancellationToken ct)
    {
        foreach (var token in _items.Values.Where(t => t.TenantId == tenantId && t.UserId == userId && !t.IsRevoked))
            token.RevokedAt = revokedAt;

        return Task.CompletedTask;
    }
}
