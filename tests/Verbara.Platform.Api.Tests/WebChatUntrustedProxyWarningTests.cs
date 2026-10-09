using System.Net;
using Verbara.Platform.Api.Middleware;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// With no trusted proxy configured, a request from a private or loopback address most likely came
/// through a proxy the app does not trust, so every visitor shares that address and one per-client
/// WebChat budget. The app says so once, naming the setting that fixes it.
/// </summary>
public sealed class WebChatUntrustedProxyWarningTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.4")]
    [InlineData("172.31.250.10")]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void IsLikelyUntrustedProxy_ShouldBeTrue_WhenNoProxyIsTrustedAndThePeerIsPrivate(string peer)
    {
        WebChatRateLimitPolicy.IsLikelyUntrustedProxy(IPAddress.Parse(peer), trustedProxiesConfigured: false)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:db8::1")]
    public void IsLikelyUntrustedProxy_ShouldBeFalse_WhenThePeerIsPublic(string peer)
    {
        WebChatRateLimitPolicy.IsLikelyUntrustedProxy(IPAddress.Parse(peer), trustedProxiesConfigured: false)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyUntrustedProxy_ShouldBeFalse_WhenAProxyIsTrusted()
    {
        WebChatRateLimitPolicy.IsLikelyUntrustedProxy(IPAddress.Parse("10.0.0.1"), trustedProxiesConfigured: true)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyUntrustedProxy_ShouldBeFalse_WhenThePeerIsUnknown()
    {
        WebChatRateLimitPolicy.IsLikelyUntrustedProxy(null, trustedProxiesConfigured: false).Should().BeFalse();
    }

    [Fact]
    public void UntrustedProxyWarning_ShouldLogOneWarningNamingTheSetting_WhenManyRequestsComeFromAPrivatePeer()
    {
        var logger = new RecordingLogger();
        var warning = new WebChatRateLimitPolicy.UntrustedProxyWarning(logger, trustedProxiesConfigured: false);

        warning.Observe(IPAddress.Parse("203.0.113.7"));
        logger.Entries.Should().BeEmpty("a public peer is a real client address");

        warning.Observe(IPAddress.Parse("10.0.0.1"));
        warning.Observe(IPAddress.Parse("10.0.0.2"));

        logger.Entries.Should().ContainSingle().Which.Should().Match<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains("ForwardedHeaders:TrustedProxies", StringComparison.Ordinal));
    }

    [Fact]
    public void UntrustedProxyWarning_ShouldLogNothing_WhenAProxyIsTrusted()
    {
        var logger = new RecordingLogger();
        var warning = new WebChatRateLimitPolicy.UntrustedProxyWarning(logger, trustedProxiesConfigured: true);

        warning.Observe(IPAddress.Parse("10.0.0.1"));

        logger.Entries.Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
