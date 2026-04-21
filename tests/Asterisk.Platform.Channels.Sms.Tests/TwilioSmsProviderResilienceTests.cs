using System.Net;
using Asterisk.Platform.Channels.Sms.Providers;
using Asterisk.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Sms.Tests;

public class TwilioSmsProviderResilienceTests
{
    [Fact]
    public void ResiliencePolicyKey_ShouldMatchContractedName()
    {
        // Keyed singleton registered by AddTwilioResiliencePolicy() depends on this exact string.
        TwilioSmsProvider.ResiliencePolicyKey.Should().Be("channel.twilio-sms");
    }

    [Fact]
    public async Task SendAsync_ShouldInvokeHttpSendOnce_WhenDeliverySucceeds()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.Created, failFirstAttempts: 0);

        var result = await provider.SendAsync(
            from: "+15550001111",
            recipient: "+15559998888",
            body: "hello",
            CancellationToken.None);

        handler.Attempts.Should().Be(1);
        result.Success.Should().BeTrue();
        result.MessageId.Should().Be("SMxxxxxx");
    }

    [Fact]
    public async Task SendAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransientError()
    {
        var (provider, handler) = CreateProvider(HttpStatusCode.Created, failFirstAttempts: 1);

        var result = await provider.SendAsync(
            from: "+15550001111",
            recipient: "+15559998888",
            body: "hello",
            CancellationToken.None);

        handler.Attempts.Should().Be(2, "first attempt threw, policy retried and succeeded");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void AddTwilioResiliencePolicy_ShouldRegisterKeyedPolicy()
    {
        var services = new ServiceCollection();
        services.AddTwilioResiliencePolicy();
        using var sp = services.BuildServiceProvider();

        var policy = sp.GetKeyedService<ResiliencePolicy>(TwilioSmsProvider.ResiliencePolicyKey);
        policy.Should().NotBeNull();
    }

    private static (TwilioSmsProvider provider, CountingHandler handler) CreateProvider(
        HttpStatusCode status, int failFirstAttempts)
    {
        var handler = new CountingHandler(status, failFirstAttempts);
        var factory = new SingleClientFactory(handler);
        var options = Options.Create(new TwilioOptions
        {
            AccountSid = "AC_test",
            AuthToken = "auth_token",
        });
        // Use the real policy with tiny backoff so retry tests stay fast.
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var provider = new TwilioSmsProvider(factory, options, policy);
        return (provider, handler);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly int _failFirstCount;
        private int _attempts;
        private const string SuccessBody = """{"sid":"SMxxxxxx","status":"queued"}""";

        public CountingHandler(HttpStatusCode status, int failFirstCount)
        {
            _status = status;
            _failFirstCount = failFirstCount;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failFirstCount)
                throw new HttpRequestException($"transient failure (attempt {attempt})");

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(SuccessBody),
            });
        }
    }
}
