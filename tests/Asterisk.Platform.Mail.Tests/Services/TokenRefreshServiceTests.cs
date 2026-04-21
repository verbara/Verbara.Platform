using System.Net;
using System.Text;
using Asterisk.Platform.Mail.Services;
using Asterisk.Sdk.Resilience;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Mail.Tests.Services;

public sealed class TokenRefreshServiceTests
{
    private const string TokenResponseJson = """
        {"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer"}
        """;

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Microsoft:ClientId"] = "test-client-id",
                ["Microsoft:ClientSecret"] = "test-client-secret",
                ["Microsoft:TenantId"] = "common",
            })
            .Build();

    private static OAuthToken BuildExpiringToken() => new()
    {
        Id = "1",
        TenantId = "tenant-1",
        UserId = "user-1",
        Provider = "microsoft",
        AccessToken = "old",
        RefreshToken = "refresh-token-123",
        ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        Scopes = "Mail.Read",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow.AddHours(-1),
    };

    private static (TokenRefreshService Service, CountingHttpHandler Handler, FakeTokenStore Store, RecordingLogger Logger) BuildSut(
        ResiliencePolicy policy, int failFirstCount, bool alwaysFail = false)
    {
        var handler = new CountingHttpHandler(TokenResponseJson, failFirstCount, alwaysFail);
        var httpClientFactory = new StaticHttpClientFactory(handler);
        var store = new FakeTokenStore();
        store.Seed(BuildExpiringToken());
        var logger = new RecordingLogger();

        var service = new TokenRefreshService(
            store,
            httpClientFactory,
            BuildConfig(),
            logger,
            policy);

        return (service, handler, store, logger);
    }

    [Fact]
    public async Task RefreshExpiringTokens_ShouldInvokeHttpSendOnce_WhenRefreshSucceeds()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler, store, _) = BuildSut(policy, failFirstCount: 0);

        await sut.RefreshExpiringTokensForTestAsync(CancellationToken.None);

        handler.Attempts.Should().Be(1);
        store.UpsertedTokens.Should().HaveCount(1);
        store.UpsertedTokens[0].AccessToken.Should().Be("new-access");
    }

    [Fact]
    public async Task RefreshExpiringTokens_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransient()
    {
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler, store, _) = BuildSut(policy, failFirstCount: 1);

        await sut.RefreshExpiringTokensForTestAsync(CancellationToken.None);

        handler.Attempts.Should().Be(2, "policy retried once after the transient failure");
        store.UpsertedTokens.Should().HaveCount(1);
    }

    [Fact]
    public async Task RefreshExpiringTokens_ShouldEmitWarningLog_WhenRetriesExhausted()
    {
        // Previously this path silently-swallowed the exception. After the resilience wrap,
        // policy exhausts and we emit a structured Warning so operators see persistent failures.
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var (sut, handler, store, logger) = BuildSut(policy, failFirstCount: 0, alwaysFail: true);

        await sut.RefreshExpiringTokensForTestAsync(CancellationToken.None);

        handler.Attempts.Should().Be(2, "policy attempted twice before giving up");
        store.UpsertedTokens.Should().BeEmpty("no successful refresh should write to the store");

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("exhausted retries", StringComparison.OrdinalIgnoreCase),
            "exhaustion should surface as a structured Warning rather than silent swallow");
    }

    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        TokenRefreshService.ResiliencePolicyKey.Should().Be("mail.token-refresh");
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly int _failFirstCount;
        private readonly bool _alwaysFail;
        private int _attempts;

        public CountingHttpHandler(string body, int failFirstCount, bool alwaysFail)
        {
            _body = body;
            _failFirstCount = failFirstCount;
            _alwaysFail = alwaysFail;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _attempts);
            if (_alwaysFail || n <= _failFirstCount)
                throw new HttpRequestException($"transient token-endpoint failure (attempt {n})");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StaticHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeTokenStore : TokenStore
    {
        private readonly List<OAuthToken> _expiring = new();
        public List<OAuthToken> UpsertedTokens { get; } = new();

        public void Seed(OAuthToken token) => _expiring.Add(token);

        public override Task<IReadOnlyList<OAuthToken>> GetExpiringAsync(TimeSpan window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OAuthToken>>(_expiring);

        public override Task UpsertAsync(OAuthToken token, CancellationToken ct)
        {
            UpsertedTokens.Add(token);
            return Task.CompletedTask;
        }

        public override Task<OAuthToken?> GetAsync(string tenantId, string userId, string provider, CancellationToken ct) =>
            Task.FromResult<OAuthToken?>(null);

        public override Task DeleteAsync(string tenantId, string userId, string provider, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger<TokenRefreshService>
    {
        public List<LogEntry> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
