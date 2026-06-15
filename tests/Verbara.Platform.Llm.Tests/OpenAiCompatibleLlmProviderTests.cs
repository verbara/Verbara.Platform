using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Llm;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

public sealed class OpenAiCompatibleLlmProviderTests
{
    private static LlmProviderOptions ConfiguredOptions() => new()
    {
        BaseUrl = "https://api.example.test/v1/",
        ApiKey = "sk-test-key",
        Model = "gpt-test",
    };

    private static OpenAiCompatibleLlmProvider CreateProvider(
        CapturingHandler handler,
        LlmProviderOptions? options = null,
        IMeterFactory? meterFactory = null)
    {
        var http = new HttpClient(handler);
        return new OpenAiCompatibleLlmProvider(
            http,
            Options.Create(options ?? ConfiguredOptions()),
            meterFactory: meterFactory);
    }

    private static LlmRequest SampleRequest() => new(
        [new LlmMessage("system", "Classify this."), new LlmMessage("user", "Hi there")],
        Temperature: 0.3,
        MaxTokens: 64);

    [Fact]
    public async Task CompleteAsync_ShouldPostChatCompletionAndParseContent_WhenProviderResponds()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"hello"}}]}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Content.Should().Be("hello");
        response.Usage.Should().BeNull();

        handler.Request.Should().NotBeNull();
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString()
            .Should().Be("https://api.example.test/v1/chat/completions");
        handler.Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Request.Headers.Authorization.Parameter.Should().Be("sk-test-key");

        handler.RequestBody.Should().Contain("\"model\":\"gpt-test\"");
        handler.RequestBody.Should().Contain("\"max_tokens\":64");
        handler.RequestBody.Should().Contain("\"temperature\":0.3");
        handler.RequestBody.Should().Contain("\"role\":\"system\"");
        handler.RequestBody.Should().Contain("\"content\":\"Classify this.\"");
        handler.RequestBody.Should().Contain("\"content\":\"Hi there\"");
    }

    [Fact]
    public async Task CompleteAsync_ShouldThrow_WhenHttpErrorStatus()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "boom");
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnEmptyContent_WhenNoChoices()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Content.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnTokenUsage_WhenProviderReturnsUsageBlock()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":120,"completion_tokens":40,"total_tokens":160}}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(120);
        response.Usage.CompletionTokens.Should().Be(40);
        response.Usage.TotalTokens.Should().Be(160);
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnNullUsage_WhenBodyOmitsUsage()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Usage.Should().BeNull();
    }

    // ── Meter tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_ShouldIncrementRequestAndTokenCounters_WhenCalled()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new CollectingMeterListener(LlmMetrics.MeterName);

        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""");
        var provider = CreateProvider(handler, meterFactory: meterFactory);

        await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        listener.Counters.Should().ContainKey("llm.requests");
        listener.Counters["llm.requests"].Should().Be(1);
        listener.Counters.Should().ContainKey("llm.tokens.in");
        listener.Counters["llm.tokens.in"].Should().Be(10);
        listener.Counters.Should().ContainKey("llm.tokens.out");
        listener.Counters["llm.tokens.out"].Should().Be(5);
    }

    [Fact]
    public async Task CompleteAsync_ShouldIncrementErrorCounter_WhenProviderFails()
    {
        using var meterFactory = new TestMeterFactory();
        using var listener = new CollectingMeterListener(LlmMetrics.MeterName);

        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "boom");
        var provider = CreateProvider(handler, meterFactory: meterFactory);

        var act = () => provider.CompleteAsync(SampleRequest(), CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();

        listener.Counters.Should().ContainKey("llm.errors");
        listener.Counters["llm.errors"].Should().Be(1);
    }

    // ── Test helpers ─────────────────────────────────────────────────────────

    private sealed class CollectingMeterListener : IDisposable
    {
        private readonly MeterListener _listener;
        public Dictionary<string, long> Counters { get; } = new();

        public CollectingMeterListener(string meterName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                lock (Counters)
                    Counters[instrument.Name] = Counters.GetValueOrDefault(instrument.Name) + value;
            });
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                // Histogram — not asserted numerically, just ensure no crash
                _ = value;
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var m in _meters) m.Dispose();
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
