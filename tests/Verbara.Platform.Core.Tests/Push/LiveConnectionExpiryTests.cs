using System.Globalization;
using System.Security.Claims;
using Verbara.Platform.Core.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Verbara.Platform.Core.Tests.Push;

/// <summary>
/// The one rule every live connection — a Realtime hub connection, an Api SSE stream — is closed
/// by: no later than the expiry of the credential that opened it.
/// </summary>
public sealed class LiveConnectionExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // ─── Of: when a principal's credential expires ───────────────────────────

    [Fact]
    public void Of_ShouldReadTheExpClaim_WhenThePrincipalCarriesOne()
    {
        var expiresAt = Now + TimeSpan.FromMinutes(15);

        LiveConnectionExpiry.Of(PrincipalWith(expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)))
            .Should().Be(expiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData(" 1790000000")]
    [InlineData("253402300800")] // one second past the latest instant a DateTimeOffset holds
    public void Of_ShouldCountAsAlreadyExpired_WhenTheExpClaimIsMissingOrUnreadable(string? exp)
    {
        LiveConnectionExpiry.Of(PrincipalWith(exp)).Should().Be(DateTimeOffset.MinValue,
            because: "a connection nothing bounds must not stand");
    }

    [Fact]
    public void Of_ShouldCountAsAlreadyExpired_WhenThereIsNoPrincipal()
    {
        LiveConnectionExpiry.Of(null).Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void Of_ShouldReadTheLatestRepresentableInstant_WhenExpIsAtTheLimit()
    {
        LiveConnectionExpiry.Of(PrincipalWith("253402300799"))
            .Should().Be(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
    }

    // ─── ScheduleClose: closing at that expiry ───────────────────────────────

    [Fact]
    public void ScheduleClose_ShouldCloseAtTheExpiry_AndNotBefore()
    {
        var time = new FakeTimeProvider(Now);
        var closes = 0;
        using var timer = LiveConnectionExpiry.ScheduleClose(time, Now + TimeSpan.FromMinutes(15), () => closes++, NullLogger.Instance);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));
        closes.Should().Be(0);

        time.Advance(TimeSpan.FromTicks(1));
        closes.Should().Be(1);

        time.Advance(TimeSpan.FromDays(1));
        closes.Should().Be(1, because: "a connection is closed once");
    }

    [Fact]
    public void ScheduleClose_ShouldCloseAtOnce_WhenTheExpiryHasAlreadyPassed()
    {
        var time = new FakeTimeProvider(Now);
        var closes = 0;

        using var timer = LiveConnectionExpiry.ScheduleClose(time, Now - TimeSpan.FromSeconds(5), () => closes++, NullLogger.Instance);

        closes.Should().Be(1);
    }

    [Fact]
    public void ScheduleClose_ShouldNotClose_WhenTheTimerIsDisposedFirst()
    {
        var time = new FakeTimeProvider(Now);
        var closes = 0;
        var timer = LiveConnectionExpiry.ScheduleClose(time, Now + TimeSpan.FromMinutes(15), () => closes++, NullLogger.Instance);

        timer.Dispose();
        time.Advance(TimeSpan.FromMinutes(15));

        closes.Should().Be(0, because: "the connection ended before its credential");
    }

    [Fact]
    public void ScheduleClose_ShouldCloseNoLaterThanTheExpiry_WhenItIsBeyondTheLongestTimerDueTime()
    {
        // A timer cannot be armed further out than ~49.7 days; an expiry beyond that must not make
        // scheduling throw (that would fail the connection open), and must not be ignored either.
        var time = new FakeTimeProvider(Now);
        var closes = 0;

        var act = () => LiveConnectionExpiry.ScheduleClose(time, DateTimeOffset.MaxValue, () => closes++, NullLogger.Instance);

        using var timer = act.Should().NotThrow().Subject;
        time.Advance(TimeSpan.FromDays(50));
        closes.Should().Be(1, because: "closing early is within 'no later than the expiry'");
    }

    [Fact]
    public void ScheduleClose_ShouldSwallowTheClose_WhenTheConnectionIsAlreadyGone()
    {
        var time = new FakeTimeProvider(Now);
        var logger = new RecordingLogger();
        using var timer = LiveConnectionExpiry.ScheduleClose(
            time, Now + TimeSpan.FromMinutes(1), () => throw new ObjectDisposedException("connection"), logger);

        var act = () => time.Advance(TimeSpan.FromMinutes(1));

        act.Should().NotThrow(because: "the close runs on a timer thread, where an escaping exception ends the process");
        logger.Errors.Should().BeEmpty(because: "a connection that finished tearing down is what the close wanted");
    }

    [Fact]
    public void ScheduleClose_ShouldLogTheFailure_WhenClosingTheConnectionThrows()
    {
        var time = new FakeTimeProvider(Now);
        var logger = new RecordingLogger();
        var failure = new InvalidOperationException("transport failed");
        using var timer = LiveConnectionExpiry.ScheduleClose(time, Now + TimeSpan.FromMinutes(1), () => throw failure, logger);

        var act = () => time.Advance(TimeSpan.FromMinutes(1));

        act.Should().NotThrow(because: "the close runs on a timer thread, where an escaping exception ends the process");
        logger.Errors.Should().ContainSingle().Which.Should().BeSameAs(failure);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static ClaimsPrincipal PrincipalWith(string? exp)
    {
        List<Claim> claims = [new Claim("sub", "alice"), new Claim("tid", "acme")];
        if (exp is not null)
            claims.Add(new Claim("exp", exp, ClaimValueTypes.Integer64));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<Exception> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
                Errors.Add(exception);
        }
    }
}
