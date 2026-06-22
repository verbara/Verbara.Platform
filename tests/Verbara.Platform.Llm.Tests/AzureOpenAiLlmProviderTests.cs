using System.Net;
using FluentAssertions;
using Verbara.Platform.Llm;
using Xunit;

namespace Verbara.Platform.Llm.Tests;

public sealed class AzureOpenAiLlmProviderTests
{
    private static LlmEffectiveOptions Effective() => new(
        BaseUrl: "https://my-resource.openai.azure.com/",
        ApiKey: "azure-secret-key",
        Model: "gpt-4o",
        Temperature: 0.2,
        MaxTokens: 800,
        TimeoutSeconds: 20);

    private static AzureOpenAiLlmProvider CreateProvider(
        StubHttpMessageHandler handler,
        string deployment = "prod-gpt4o",
        string apiVersion = "2024-06-01") =>
        new(new HttpClient(handler), Effective(), deployment, apiVersion);

    private static LlmRequest SampleRequest() => new(
        [new LlmMessage("system", "Classify."), new LlmMessage("user", "Hello")],
        Temperature: 0.3,
        MaxTokens: 64);

    [Fact]
    public async Task CompleteAsync_ShouldSendApiKeyHeader_AndDeploymentUrlWithApiVersion()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var provider = CreateProvider(handler, deployment: "prod-gpt4o", apiVersion: "2024-06-01");

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Content.Should().Be("ok");

        handler.Request.Should().NotBeNull();
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be(
            "https://my-resource.openai.azure.com/openai/deployments/prod-gpt4o/chat/completions?api-version=2024-06-01");

        // Azure uses the api-key header, NOT Authorization: Bearer.
        handler.Request.Headers.Authorization.Should().BeNull();
        handler.Request.Headers.GetValues("api-key").Should().ContainSingle()
            .Which.Should().Be("azure-secret-key");

        handler.RequestBody.Should().Contain("\"model\":\"gpt-4o\"");
        handler.RequestBody.Should().Contain("\"max_tokens\":64");
    }

    [Fact]
    public async Task CompleteAsync_ShouldReturnTokenUsage_WhenProviderReturnsUsageBlock()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}""");
        var provider = CreateProvider(handler);

        var response = await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        response.Usage.Should().NotBeNull();
        response.Usage!.PromptTokens.Should().Be(11);
        response.Usage.CompletionTokens.Should().Be(7);
        response.Usage.TotalTokens.Should().Be(18);
    }

    [Fact]
    public async Task CompleteAsync_ShouldThrow_WhenHttpErrorStatus()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "nope");
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteAsync_ShouldEscapeDeploymentAndApiVersion_InUrl()
    {
        // A deployment name / api-version with URL-significant characters must be escaped so they
        // can't break out of their URL segment / query value.
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var provider = CreateProvider(handler, deployment: "my deploy/2", apiVersion: "2024-06-01&x");

        await provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        // AbsoluteUri preserves the percent-encoding (ToString() cosmetically decodes %20). The
        // load-bearing escapes: '/' → %2F (can't break the path segment) and '&' → %26 (can't break
        // the query).
        var url = handler.Request!.RequestUri!.AbsoluteUri;
        url.Should().Contain("/openai/deployments/my%20deploy%2F2/chat/completions");
        url.Should().Contain("api-version=2024-06-01%26x");
    }

    [Fact]
    public async Task CompleteAsync_ShouldOmitApiKeyHeader_WhenKeyless()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        var keylessOptions = new LlmEffectiveOptions(
            BaseUrl: "https://my-resource.openai.azure.com/", ApiKey: null, Model: "gpt-4o",
            Temperature: 0.2, MaxTokens: 800, TimeoutSeconds: 20);
        var provider = new AzureOpenAiLlmProvider(
            new HttpClient(handler), keylessOptions, "prod-gpt4o", "2024-06-01");

        var act = () => provider.CompleteAsync(SampleRequest(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        handler.Request!.Headers.Contains("api-key").Should().BeFalse();
    }
}
