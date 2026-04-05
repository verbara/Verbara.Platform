namespace Asterisk.Platform.Core;

public enum RateLimitTier
{
    Free = 60,
    Standard = 300,
    Professional = 600,
    Enterprise = 1200,
    Unlimited = 0,
}

public static class RateLimitTierExtensions
{
    public static int GetPermitLimit(this RateLimitTier tier) => (int)tier;
    public static bool IsUnlimited(this RateLimitTier tier) => tier == RateLimitTier.Unlimited;
}
