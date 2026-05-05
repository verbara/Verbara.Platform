using System.Text.Encodings.Web;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="ApiKeyAuthenticationHandler"/>.
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests
{
    private static ApiKeyAuthenticationHandler CreateHandler(
        IApiKeyStore apiKeyStore,
        IUserStore userStore,
        HttpContext context)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        options.CurrentValue.Returns(new AuthenticationSchemeOptions());

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var handler = new ApiKeyAuthenticationHandler(
            options,
            loggerFactory,
            UrlEncoder.Default,
            apiKeyStore,
            userStore);

        var scheme = new AuthenticationScheme(
            AuthSchemeConfiguration.ApiKeyScheme,
            AuthSchemeConfiguration.ApiKeyScheme,
            typeof(ApiKeyAuthenticationHandler));

        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();
        return handler;
    }

    private static DefaultHttpContext CreateContext(string? authHeader = null, string? queryTokenParam = null)
    {
        var context = new DefaultHttpContext();

        if (authHeader is not null)
            context.Request.Headers["Authorization"] = authHeader;

        if (queryTokenParam is not null)
            context.Request.QueryString = new QueryString($"?token={Uri.EscapeDataString(queryTokenParam)}");

        return context;
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnNoResult_WhenApiKeyInQueryStringOnly()
    {
        // Arrange: provide a raw API key value in ?token= with no Authorization header
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var userStore = Substitute.For<IUserStore>();

        // Even if a matching key existed in the store, the handler must not consult it
        // because the query-string fallback has been removed.
        var context = CreateContext(authHeader: null, queryTokenParam: "some-api-key-value");
        var handler = CreateHandler(apiKeyStore, userStore, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert: no Authorization header → NoResult (store never consulted)
        result.None.Should().BeTrue("the ?token= query parameter must not be used for API key auth");
        await apiKeyStore.DidNotReceive().GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnNoResult_WhenNoHeaderAndNoQuery()
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var userStore = Substitute.For<IUserStore>();
        var context = CreateContext();
        var handler = CreateHandler(apiKeyStore, userStore, context);

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAuthenticate_ShouldReturnFailure_WhenBearerKeyIsInvalid()
    {
        var apiKeyStore = Substitute.For<IApiKeyStore>();
        apiKeyStore.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ApiKey?)null);
        var userStore = Substitute.For<IUserStore>();

        var context = CreateContext(authHeader: "Bearer invalid-key");
        var handler = CreateHandler(apiKeyStore, userStore, context);

        var result = await handler.AuthenticateAsync();

        result.Failure.Should().NotBeNull();
    }
}
