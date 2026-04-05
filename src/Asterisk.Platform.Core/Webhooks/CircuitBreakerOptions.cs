namespace Asterisk.Platform.Core.Webhooks;

public sealed class CircuitBreakerOptions
{
    public int FailureThreshold { get; set; } = 10;
    public int CooldownSeconds { get; set; } = 300;
    public int MaxCooldownSeconds { get; set; } = 3600;
    public double CooldownMultiplier { get; set; } = 2.0;
}
