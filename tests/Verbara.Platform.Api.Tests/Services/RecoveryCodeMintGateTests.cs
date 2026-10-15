using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Services;

/// <summary>
/// <see cref="RecoveryCodeMintGate"/> on its own (openspec <c>harden-recovery-code-digest</c>, design D10): one
/// mint per user per interval, independent per user and tenant, off at a zero interval. The endpoint half is
/// <c>RecoveryCodeMintThrottleTests</c>.
/// </summary>
public sealed class RecoveryCodeMintGateTests
{
    private static readonly TenantId Tenant = new("t-mint");
    private static readonly EntityId User = EntityId.From("u-mint");

    [Fact]
    public void TryAcquire_ShouldRefuseWithRemainingTime_WhenSameUserMintsWithinInterval()
    {
        var time = new FakeTimeProvider();
        var gate = new RecoveryCodeMintGate(new RecoveryCodeMintOptions(TimeSpan.FromSeconds(10)), time);

        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(4));

        gate.TryAcquire(Tenant, User, out var retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void TryAcquire_ShouldAllow_WhenIntervalHasElapsed()
    {
        var time = new FakeTimeProvider();
        var gate = new RecoveryCodeMintGate(new RecoveryCodeMintOptions(TimeSpan.FromSeconds(10)), time);

        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(10));

        gate.TryAcquire(Tenant, User, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TryAcquire_ShouldAllow_WhenAnotherUserOrTenantMintedWithinInterval()
    {
        var gate = new RecoveryCodeMintGate(new RecoveryCodeMintOptions(TimeSpan.FromSeconds(10)), new FakeTimeProvider());

        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();

        gate.TryAcquire(Tenant, EntityId.From("u-other"), out _).Should().BeTrue(
            because: "one account's mint never spends another account's allowance");
        gate.TryAcquire(new TenantId("t-other"), User, out _).Should().BeTrue(
            because: "the same user id in another tenant is another account");
    }

    [Fact]
    public void TryAcquire_ShouldAlwaysAllow_WhenIntervalIsZero()
    {
        var gate = new RecoveryCodeMintGate(new RecoveryCodeMintOptions(TimeSpan.Zero), new FakeTimeProvider());

        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();
        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_ShouldStillRefuseRecentMints_WhenExpiredEntriesAreSwept()
    {
        var time = new FakeTimeProvider();
        var gate = new RecoveryCodeMintGate(new RecoveryCodeMintOptions(TimeSpan.FromSeconds(10)), time);
        for (var i = 0; i < 1024; i++)
            gate.TryAcquire(Tenant, EntityId.From($"u-old-{i}"), out _).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(5));
        gate.TryAcquire(Tenant, User, out _).Should().BeTrue();  // sweeps nothing: none has expired yet

        time.Advance(TimeSpan.FromSeconds(6));                    // the 1024 have expired; User has not
        gate.TryAcquire(Tenant, EntityId.From("u-new"), out _).Should().BeTrue(); // this one sweeps

        gate.TryAcquire(Tenant, User, out _).Should().BeFalse(because: "a sweep removes only expired entries");
        gate.TryAcquire(Tenant, EntityId.From("u-old-0"), out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("not-a-timespan", 10)]
    [InlineData("-00:00:05", 10)]
    [InlineData("00:00:00", 0)]
    [InlineData("00:01:00", 60)]
    public void From_ShouldReadIntervalOrFallBackToDefault_WhenConfigured(string? value, int expectedSeconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Mfa:RecoveryCodeMint:MinInterval"] = value })
            .Build();

        RecoveryCodeMintOptions.From(configuration).MinInterval.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(5.2, "6")]
    [InlineData(0.01, "1")]
    public void Refuse_ShouldSetRetryAfterInWholeSeconds_WhenRefusing(double seconds, string expected)
    {
        var context = new DefaultHttpContext();

        var result = RecoveryCodeMintGate.Refuse(context, TimeSpan.FromSeconds(seconds));

        context.Response.Headers.RetryAfter.ToString().Should().Be(expected);
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }
}
