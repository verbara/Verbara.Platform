using System.Net;
using FluentAssertions;
using Verbara.Platform.Llm;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

public sealed class AnthropicLlmProviderTests
{
    private static LlmEffectiveOptions Effective(string? baseUrl = null) => new(
        BaseUrl: baseUrl,
        ApiKey: "anthropic-secret-key",
        Model: "claude-3-5-haiku-latest",
        Temperature: 0.2,
        MaxTokens: 800,
        TimeoutSeconds: 20);

    private static AnthropicLlmProvider CreateProvider(
        StubHttpMessageHandler handler,
        string? baseUrl = null,
        string? anthropicVersion = null) =>
        new(new HttpClient(handler), Effective(baseUrl), anthropicVersion);

    private static LlmRequest SampleRequest() => new(
        [
            new LlmMessage("system", "You are a classifier."),
            new LlmMessage("user", "Hello"),
            new LlmMessage("assistant", "Hi"),
        ],
        Temperature: 0.3,
        MaxTokens: 128);

    [Fact]
    public async Task CompleteAsync_ShouldSendXApiKeyAndAnthropicVersion_AndHoistSystemTurn()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"ok"}]}""");
        var provider = CreateProvider(handler);

        await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        handler.Request.Should().NotBeNull();
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        // Default base URL + Messages path.
        handler.Request.RequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/messages");

        // Anthropic auth headers (NOT Authorization: Bearer).
        handler.Request.Headers.Authorization.Should().BeNull();
        handler.Request.Headers.GetValues("x-api-key").Should().ContainSingle()
            .Which.Should().Be("anthropic-secret-key");
        handler.Request.Headers.GetValues("anthropic-version").Should().ContainSingle()
            .Which.Should().Be("2023-06-01");

        // System turn hoisted to the top-level "system" field, NOT a messages[] entry.
        handler.RequestBody.Should().Contain("\"system\":\"You are a classifier.\"");
        handler.RequestBody.Should().Contain("\"model\":\"claude-3-5-haiku-latest\"");
        handler.RequestBody.Should().Contain("\"max_tokens\":128");
        // The remaining (non-system) turns stay in messages[].
        handler.RequestBody.Should().Contain("\"role\":\"user\"");
        handler.RequestBody.Should().Contain("\"role\":\"assistant\"");
        // No system-role turn leaks into messages[].
        handler.RequestBody.Should().NotContain("\"role\":\"system\"");
    }

    [Fact]
    public async Task CompleteAsync_ShouldUseSuppliedVersionAndBaseUrl_WhenProvided()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"ok"}]}""");
        var provider = CreateProvider(
            handler,
            baseUrl: "https://anthropic.proxy.test/",
            anthropicVersion: "2024-10-22");

        await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        handler.Request!.RequestUri!.ToString().Should().Be("https://anthropic.proxy.test/v1/messages");
        handler.Request.Headers.GetValues("anthropic-version").Should().ContainSingle()
            .Which.Should().Be("2024-10-22");
    }

    [Fact]
    public async Task CompleteAsync_ShouldMapContentAndUsage_FromMessagesResponse()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"classified: billing"}],"usage":{"input_tokens":42,"output_tokens":9}}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Content.Should().Be("classified: billing");
        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(42);
        response.Usage.CompletionTokens.Should().Be(9);
        // Anthropic reports no total; the provider derives it from input + output.
        response.Usage.TotalTokens.Should().Be(51);
    }

    [Fact]
    public async Task CompleteAsync_ShouldThrow_WhenHttpErrorStatus()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "slow down");
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
