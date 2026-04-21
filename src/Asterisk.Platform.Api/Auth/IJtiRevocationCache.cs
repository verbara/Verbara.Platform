namespace Asterisk.Platform.Api.Auth;

internal interface IJtiRevocationCache
{
    ValueTask<bool> IsRevokedAsync(string jti, CancellationToken ct);
    ValueTask RevokeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct);
}
