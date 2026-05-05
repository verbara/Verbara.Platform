using Verbara.Platform.Core.Webhooks;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Core.Tests.Webhooks;

public class CircuitBreakerPolicyTests
{
    private static readonly CircuitBreakerOptions DefaultOptions = new()
    {
        FailureThreshold = 3,
        CooldownSeconds = 60,
        MaxCooldownSeconds = 600,
        CooldownMultiplier = 2.0,
    };

    private static CircuitBreakerPolicy CreatePolicy(CircuitBreakerOptions? options = null)
        => new(Options.Create(options ?? DefaultOptions));

    private static WebhookSubscription ClosedSub(int failures = 0) =>
        new("sub-1", "tenant-1", "Test", "https://example.com", "secret",
            Array.Empty<string>(), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            CircuitStatus: CircuitStatus.Closed, CircuitFailures: failures);

    private static WebhookSubscription OpenSub(DateTimeOffset? nextProbeAt = null, int probeAttempts = 0) =>
        new("sub-1", "tenant-1", "Test", "https://example.com", "secret",
            Array.Empty<string>(), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            CircuitStatus: CircuitStatus.Open,
            CircuitFailures: 3,
            CircuitOpenedAt: DateTimeOffset.UtcNow.AddSeconds(-120),
            CircuitNextProbeAt: nextProbeAt,
            CircuitProbeAttempts: probeAttempts);

    private static WebhookSubscription HalfOpenSub(int probeAttempts = 1) =>
        new("sub-1", "tenant-1", "Test", "https://example.com", "secret",
            Array.Empty<string>(), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            CircuitStatus: CircuitStatus.HalfOpen,
            CircuitFailures: 3,
            CircuitOpenedAt: DateTimeOffset.UtcNow.AddSeconds(-120),
            CircuitNextProbeAt: null,
            CircuitProbeAttempts: probeAttempts);

    // ── ShouldDeliver ──────────────────────────────────────────────────────────

    [Fact]
    public void ShouldDeliver_ShouldReturnTrue_WhenClosed()
    {
        var sub = ClosedSub();
        var result = CircuitBreakerPolicy.ShouldDeliver(sub, DateTimeOffset.UtcNow);
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldDeliver_ShouldReturnTrue_WhenHalfOpen()
    {
        var sub = HalfOpenSub();
        var result = CircuitBreakerPolicy.ShouldDeliver(sub, DateTimeOffset.UtcNow);
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldDeliver_ShouldReturnFalse_WhenOpenAndCooldownNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = OpenSub(nextProbeAt: now.AddSeconds(30));
        var result = CircuitBreakerPolicy.ShouldDeliver(sub, now);
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldDeliver_ShouldReturnTrue_WhenOpenAndCooldownExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = OpenSub(nextProbeAt: now.AddSeconds(-1));
        var result = CircuitBreakerPolicy.ShouldDeliver(sub, now);
        result.Should().BeTrue();
    }

    // ── OnFailure (Closed) ─────────────────────────────────────────────────────

    [Fact]
    public void OnFailure_ShouldIncrementFailures_WhenBelowThreshold()
    {
        var policy = CreatePolicy();
        var sub = ClosedSub(failures: 1);
        var (updated, justOpened) = policy.OnFailure(sub, DateTimeOffset.UtcNow);
        updated.CircuitFailures.Should().Be(2);
        updated.CircuitStatus.Should().Be(CircuitStatus.Closed);
        justOpened.Should().BeFalse();
    }

    [Fact]
    public void OnFailure_ShouldOpenCircuit_WhenThresholdReached()
    {
        var policy = CreatePolicy();
        var now = DateTimeOffset.UtcNow;
        // 2 existing failures + 1 new = 3 = FailureThreshold
        var sub = ClosedSub(failures: 2);
        var (updated, justOpened) = policy.OnFailure(sub, now);
        updated.CircuitStatus.Should().Be(CircuitStatus.Open);
        updated.CircuitFailures.Should().Be(3);
        updated.CircuitOpenedAt.Should().Be(now);
        updated.CircuitNextProbeAt.Should().Be(now.AddSeconds(60));
        updated.CircuitProbeAttempts.Should().Be(0);
        justOpened.Should().BeTrue();
    }

    [Fact]
    public void OnFailure_ShouldNotChangeState_WhenAlreadyOpen()
    {
        var policy = CreatePolicy();
        var now = DateTimeOffset.UtcNow;
        var nextProbe = now.AddSeconds(30);
        var sub = OpenSub(nextProbeAt: nextProbe);
        var (updated, justOpened) = policy.OnFailure(sub, now);
        updated.CircuitStatus.Should().Be(CircuitStatus.Open);
        updated.CircuitNextProbeAt.Should().Be(nextProbe);
        justOpened.Should().BeFalse();
    }

    // ── OnFailure (HalfOpen) ───────────────────────────────────────────────────

    [Fact]
    public void OnFailure_ShouldReopenWithDoubledCooldown_WhenHalfOpen()
    {
        var policy = CreatePolicy();
        var now = DateTimeOffset.UtcNow;
        // probeAttempts = 0 in HalfOpen → after failure probeAttempts becomes 1
        // cooldown = 60 * 2^1 = 120s
        var sub = HalfOpenSub(probeAttempts: 0);
        var (updated, justOpened) = policy.OnFailure(sub, now);
        updated.CircuitStatus.Should().Be(CircuitStatus.Open);
        updated.CircuitProbeAttempts.Should().Be(1);
        updated.CircuitOpenedAt.Should().Be(now);
        updated.CircuitNextProbeAt.Should().Be(now.AddSeconds(120));
        justOpened.Should().BeTrue();
    }

    // ── OnSuccess ─────────────────────────────────────────────────────────────

    [Fact]
    public void OnSuccess_ShouldCloseCircuit_WhenHalfOpen()
    {
        var sub = HalfOpenSub();
        var updated = CircuitBreakerPolicy.OnSuccess(sub);
        updated.CircuitStatus.Should().Be(CircuitStatus.Closed);
        updated.CircuitFailures.Should().Be(0);
        updated.CircuitOpenedAt.Should().BeNull();
        updated.CircuitNextProbeAt.Should().BeNull();
        updated.CircuitProbeAttempts.Should().Be(0);
    }

    // ── TransitionIfCooldownExpired ────────────────────────────────────────────

    [Fact]
    public void TransitionIfCooldownExpired_ShouldMoveToHalfOpen_WhenCooldownExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = OpenSub(nextProbeAt: now.AddSeconds(-1));
        var updated = CircuitBreakerPolicy.TransitionIfCooldownExpired(sub, now);
        updated.CircuitStatus.Should().Be(CircuitStatus.HalfOpen);
    }

    [Fact]
    public void TransitionIfCooldownExpired_ShouldRemainOpen_WhenCooldownNotExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = OpenSub(nextProbeAt: now.AddSeconds(30));
        var updated = CircuitBreakerPolicy.TransitionIfCooldownExpired(sub, now);
        updated.CircuitStatus.Should().Be(CircuitStatus.Open);
    }

    [Fact]
    public void TransitionIfCooldownExpired_ShouldReturnUnchanged_WhenClosed()
    {
        var sub = ClosedSub();
        var updated = CircuitBreakerPolicy.TransitionIfCooldownExpired(sub, DateTimeOffset.UtcNow);
        updated.Should().BeSameAs(sub);
    }

    // ── Exponential backoff cap ────────────────────────────────────────────────

    [Fact]
    public void OnFailure_ShouldCapCooldownAtMax_WhenExponentialBackoffExceedsMax()
    {
        var policy = CreatePolicy();
        var now = DateTimeOffset.UtcNow;
        // probeAttempts=0 → new probeAttempts=1 → cooldown = 60 * 2^1 = 120
        // probeAttempts=3 → new probeAttempts=4 → cooldown = 60 * 2^4 = 960 > MaxCooldownSeconds(600) → capped at 600
        var sub = HalfOpenSub(probeAttempts: 3);
        var (updated, _) = policy.OnFailure(sub, now);
        updated.CircuitNextProbeAt.Should().Be(now.AddSeconds(600));
    }
}
