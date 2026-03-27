using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class SessionService
{
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly AuthEventService _authEvents;

    public SessionService(IRefreshTokenStore refreshTokenStore, AuthEventService authEvents)
    {
        _refreshTokenStore = refreshTokenStore;
        _authEvents = authEvents;
    }

    public async Task<IReadOnlyList<ActiveSession>> ListActiveSessionsAsync(
        string tenantId, string userId, CancellationToken ct)
    {
        var tokens = await _refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);
        return tokens.Select(t => new ActiveSession
        {
            TokenId = t.TokenId,
            IpAddress = t.IpAddress,
            UserAgent = t.UserAgent,
            CreatedAt = t.CreatedAt,
            ExpiresAt = t.ExpiresAt,
        }).ToList();
    }

    public async Task RevokeSessionAsync(
        string tenantId,
        string userId,
        string tokenId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        await _refreshTokenStore.RevokeAsync(tokenId, DateTimeOffset.UtcNow, replacedBy: null, ct);

        await _authEvents.LogAsync(
            tenantId,
            userId,
            AuthEventTypes.SessionRevoked,
            ipAddress,
            userAgent,
            new { tokenId },
            ct);
    }
}

internal sealed class ActiveSession
{
    public required string TokenId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
