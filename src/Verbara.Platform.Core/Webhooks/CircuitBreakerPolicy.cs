using Microsoft.Extensions.Options;

namespace Verbara.Platform.Core.Webhooks;

public sealed class CircuitBreakerPolicy
{
    private readonly CircuitBreakerOptions _options;

    public CircuitBreakerPolicy(IOptions<CircuitBreakerOptions> options)
    {
        _options = options.Value;
    }

    public static bool ShouldDeliver(WebhookSubscription sub, DateTimeOffset now)
    {
        return sub.CircuitStatus switch
        {
            CircuitStatus.Closed => true,
            CircuitStatus.HalfOpen => true,
            CircuitStatus.Open => sub.CircuitNextProbeAt.HasValue && now >= sub.CircuitNextProbeAt.Value,
            _ => false
        };
    }

    public static WebhookSubscription TransitionIfCooldownExpired(WebhookSubscription sub, DateTimeOffset now)
    {
        if (sub.CircuitStatus != CircuitStatus.Open)
            return sub;

        if (!sub.CircuitNextProbeAt.HasValue || now < sub.CircuitNextProbeAt.Value)
            return sub;

        return sub with { CircuitStatus = CircuitStatus.HalfOpen };
    }

    public static WebhookSubscription OnSuccess(WebhookSubscription sub)
    {
        return sub with
        {
            CircuitStatus = CircuitStatus.Closed,
            CircuitFailures = 0,
            CircuitOpenedAt = null,
            CircuitNextProbeAt = null,
            CircuitProbeAttempts = 0
        };
    }

    public (WebhookSubscription Updated, bool JustOpened) OnFailure(WebhookSubscription sub, DateTimeOffset now)
    {
        if (sub.CircuitStatus == CircuitStatus.Closed)
        {
            var newFailures = sub.CircuitFailures + 1;
            if (newFailures >= _options.FailureThreshold)
            {
                var cooldown = ComputeCooldown(0);
                var opened = sub with
                {
                    CircuitStatus = CircuitStatus.Open,
                    CircuitFailures = newFailures,
                    CircuitOpenedAt = now,
                    CircuitNextProbeAt = now.AddSeconds(cooldown),
                    CircuitProbeAttempts = 0
                };
                return (opened, true);
            }

            return (sub with { CircuitFailures = newFailures }, false);
        }

        if (sub.CircuitStatus == CircuitStatus.HalfOpen)
        {
            var probeAttempts = sub.CircuitProbeAttempts + 1;
            var cooldown = ComputeCooldown(probeAttempts);
            var reopened = sub with
            {
                CircuitStatus = CircuitStatus.Open,
                CircuitOpenedAt = now,
                CircuitNextProbeAt = now.AddSeconds(cooldown),
                CircuitProbeAttempts = probeAttempts
            };
            return (reopened, true);
        }

        // Already Open — no state change needed
        return (sub, false);
    }

    private double ComputeCooldown(int probeAttempts)
    {
        var cooldown = _options.CooldownSeconds * Math.Pow(_options.CooldownMultiplier, probeAttempts);
        return Math.Min(cooldown, _options.MaxCooldownSeconds);
    }
}
