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
        LlmProviderOptions? options = null)
    {
        var http = new HttpClient(handler);
        return new OpenAiCompatibleLlmProvider(http, Options.Create(options ?? ConfiguredOptions()));
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
