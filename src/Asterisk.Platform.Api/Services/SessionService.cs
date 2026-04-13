using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class SessionService
{
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly IUserStore _userStore;
    private readonly AuthEventService _authEvents;

    public SessionService(
        IRefreshTokenStore refreshTokenStore,
        IUserStore userStore,
        AuthEventService authEvents)
    {
        _refreshTokenStore = refreshTokenStore;
        _userStore = userStore;
        _authEvents = authEvents;
    }

    public async Task<IReadOnlyList<ActiveSession>> ListActiveSessionsAsync(
        string tenantId, string userId, CancellationToken ct)
    {
        var user = await _userStore.GetByIdAsync(new TenantId(tenantId), EntityId.From(userId), ct);
        if (user is null)
            return Array.Empty<ActiveSession>();

        var tokens = await _refreshTokenStore.GetActiveByUserAsync(tenantId, userId, ct);
        return tokens.Select(t => MapSession(t, user)).ToList();
    }

    public async Task<IReadOnlyList<ActiveSession>> ListAllActiveSessionsAsync(
        string tenantId, CancellationToken ct)
    {
        var tokens = await _refreshTokenStore.GetActiveByTenantAsync(tenantId, ct);
        if (tokens.Count == 0)
            return Array.Empty<ActiveSession>();

        var userIds = tokens.Select(t => t.UserId).Distinct().ToList();
        var users = await _userStore.GetByIdsAsync(tenantId, userIds, ct);
        var userMap = users.ToDictionary(u => u.UserId.Value, u => u);

        var result = new List<ActiveSession>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!userMap.TryGetValue(token.UserId, out var user))
                continue;
            result.Add(MapSession(token, user));
        }
        return result;
    }

    private static ActiveSession MapSession(RefreshToken token, User user) =>
        new ActiveSession(
            SessionId: token.TokenId,
            UserId: user.UserId.Value,
            UserEmail: user.Email,
            UserDisplayName: user.DisplayName,
            IpAddress: token.IpAddress,
            UserAgent: token.UserAgent,
            CreatedAt: token.CreatedAt,
            ExpiresAt: token.ExpiresAt,
            LastActivity: token.LastActivityAt);

    public async Task<int> RevokeAllSessionsForUserAsync(
        string tenantId,
        string adminUserId,
        string targetUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var tokens = await _refreshTokenStore.GetActiveByUserAsync(tenantId, targetUserId, ct);
        await _refreshTokenStore.RevokeAllForUserAsync(tenantId, targetUserId, DateTimeOffset.UtcNow, ct);

        await _authEvents.LogAsync(
            tenantId,
            adminUserId,
            AuthEventTypes.SessionRevoked,
            ipAddress,
            userAgent,
            new Dictionary<string, string> { ["targetUserId"] = targetUserId, ["count"] = tokens.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ct);

        return tokens.Count;
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
            new Dictionary<string, string> { ["tokenId"] = tokenId },
            ct);
    }
}

internal sealed record ActiveSession(
    string SessionId,
    string UserId,
    string UserEmail,
    string UserDisplayName,
    string? IpAddress,
    string? UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastActivity);
