using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Llm;
using Verbara.Sdk.Resilience;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

/// <summary>
/// Verifies that <see cref="ServiceCollectionExtensions.AddPlatformLlm"/> registers the
/// <c>llm.completions</c> keyed <see cref="ResiliencePolicy"/> and that the provider
/// retries HTTP failures instead of silently running with NoOp.
/// </summary>
public sealed class LlmResilienceTests
{
    private static LlmProviderOptions ConfiguredOptions(int timeoutSeconds = 5) => new()
    {
        BaseUrl = "https://api.example.test/v1/",
        ApiKey = "sk-test-key",
        Model = "gpt-test",
        TimeoutSeconds = timeoutSeconds,
    };

    // ── DI registration ──────────────────────────────────────────────────────

    [Fact]
    public void AddPlatformLlm_ShouldRegisterKeyedResiliencePolicy_WhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddPlatformLlm(o =>
        {
            o.BaseUrl = "https://api.example.test/v1/";
            o.ApiKey = "sk-test-key";
            o.Model = "gpt-test";
        });

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetKeyedService<ResiliencePolicy>(OpenAiCompatibleLlmProvider.ResiliencePolicyKey);

        policy.Should().NotBeNull("AddPlatformLlm must register the llm.completions keyed policy");
    }

    [Fact]
    public void AddPlatformLlm_ShouldStillRegisterKeyedResiliencePolicy_WhenUnconfigured()
    {
        // P2c.1: the keyed llm.completions policy is now registered ALWAYS — per-tenant BYO
        // providers (built by the resolver) need it even when no global key is configured.
        var services = new ServiceCollection();
        services.AddPlatformLlm(o =>
        {
            // Deliberately incomplete: no ApiKey ⇒ no global typed-client, but the policy stays.
            o.BaseUrl = "https://api.example.test/v1/";
            o.Model = "gpt-test";
        });

        using var sp = services.BuildServiceProvider();
        var policy = sp.GetKeyedService<ResiliencePolicy>(OpenAiCompatibleLlmProvider.ResiliencePolicyKey);

        policy.Should().NotBeNull("the keyed policy is shared by per-tenant providers and is always registered");

        // But the global typed-client ILlmProvider must NOT be registered (Flows still TryAdds the stub).
        sp.GetService<ILlmProvider>().Should().BeNull("no global key ⇒ no global typed-client");
    }

    // ── Retry behaviour ──────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_ShouldRetry_WhenEndpointAlwaysFails()
    {
        // Arrange: a real retry policy with tiny backoff, always-failing handler.
        // maxAttempts = 3 means 1 initial call + 2 retries (per WithRetry XML doc).
        const int maxAttempts = 3;
        var handler = new CountingHandler(alwaysThrow: true);
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: maxAttempts, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var provider = CreateProvider(handler, policy: policy);

        // Act
        var act = () => provider.CompleteAsync(
            new LlmRequest([new LlmMessage("user", "hi")], Temperature: 0.2, MaxTokens: 64),
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("all attempts exhausted → policy rethrows");

        // Assert: policy made maxAttempts total calls (initial + 2 retries).
        handler.Attempts.Should().Be(maxAttempts,
            "policy makes {0} total attempts (1 initial + {1} retries)", maxAttempts, maxAttempts - 1);
    }

    [Fact]
    public async Task CompleteAsync_ShouldRetryAndSucceed_WhenFirstAttemptThrowsTransientError()
    {
        // Arrange: fail first attempt, succeed on second.
        var handler = new CountingHandler(failFirstAttempts: 1, successBody: """
            {"choices":[{"message":{"role":"assistant","content":"ok"}}]}
            """);
        var policy = new ResiliencePolicyBuilder()
            .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1))
            .Build();
        var provider = CreateProvider(handler, policy: policy);

        // Act
        var response = await provider.CompleteAsync(
            new LlmRequest([new LlmMessage("user", "hi")], Temperature: 0.2, MaxTokens: 64),
            CancellationToken.None);

        // Assert
        handler.Attempts.Should().Be(2, "first attempt threw; policy retried and succeeded");
        response.Content.Should().Be("ok");
    }

    // ── Without the registered policy (NoOp) the handler is called exactly once ──

    [Fact]
    public async Task CompleteAsync_ShouldInvokeHandlerOnce_WhenNoOpPolicy()
    {
        // This test documents the NoOp baseline: without a real policy there is NO retry.
        var handler = new CountingHandler(failFirstAttempts: 1, successBody: """
            {"choices":[{"message":{"role":"assistant","content":"ok"}}]}
            """);
        // Explicitly pass NoOp (simulates the pre-fix state where no keyed policy was registered).
        var provider = CreateProvider(handler, policy: ResiliencePolicy.NoOp);

        var act = () => provider.CompleteAsync(
            new LlmRequest([new LlmMessage("user", "hi")], Temperature: 0.2, MaxTokens: 64),
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>("NoOp does not retry — first failure propagates");
        handler.Attempts.Should().Be(1, "NoOp: no retry, handler called exactly once");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static OpenAiCompatibleLlmProvider CreateProvider(
        HttpMessageHandler handler,
        LlmProviderOptions? options = null,
        ResiliencePolicy? policy = null)
    {
        var http = new HttpClient(handler);
        return new OpenAiCompatibleLlmProvider(
            http,
            Options.Create(options ?? ConfiguredOptions()),
            policy: policy);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly bool _alwaysThrow;
        private readonly int _failFirstCount;
        private readonly string _successBody;
        private int _attempts;

        public CountingHandler(
            bool alwaysThrow = false,
            int failFirstAttempts = 0,
            string successBody = """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""")
        {
            _alwaysThrow = alwaysThrow;
            _failFirstCount = failFirstAttempts;
            _successBody = successBody;
        }

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);

            if (_alwaysThrow || attempt <= _failFirstCount)
                throw new HttpRequestException($"transient failure (attempt {attempt})");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _successBody,
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
