using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public sealed class ApiKey : ITenantScoped, IAuditable
{
    public required EntityId KeyId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required string HashedKey { get; init; }
    public required IReadOnlyList<string> Scopes { get; set; }
    public int? RateLimitPerMinute { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public EntityId? UserId { get; set; }
    public bool IsRevoked { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
    public ApiKeyType KeyType { get; init; } = ApiKeyType.Standard;

    public bool IsExpired(DateTimeOffset now) =>
        ExpiresAt.HasValue && now >= ExpiresAt.Value;

    public bool HasScope(string requiredScope)
    {
        foreach (var scope in Scopes)
        {
            if (scope == requiredScope)
                return true;

            if (scope.EndsWith(":*", StringComparison.Ordinal))
            {
                var prefix = scope[..^1]; // "admin:*" → "admin:"
                if (requiredScope.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
