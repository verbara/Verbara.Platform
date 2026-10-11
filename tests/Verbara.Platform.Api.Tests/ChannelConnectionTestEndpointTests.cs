using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real 6.5 (design D15, spec <c>channel-credential-config</c>):
/// <c>POST /admin/channels/whatsapp/test</c> asks the Graph API for the tenant's phone number with the
/// tenant's own token, at the configured <c>WhatsApp:BaseUrl</c>, and reports Meta's verdict; any other
/// channel is <c>not-testable</c>. Until 2.28.0 the endpoint answered "Connection test passed" for every
/// channel without calling anything.
/// </summary>
public sealed class ChannelConnectionTestEndpointTests : IClassFixture<ChannelConnectionTestEndpointTests.Factory>
{
    private const string GraphBaseUrl = "https://graph-stub.test/meta";

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public ChannelConnectionTestEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task TestConnection_ShouldReturnVerifiedName_WhenGraphAnswers200()
    {
        var token = $"EAAtest{Guid.NewGuid():N}";
        await SeedWhatsAppAsync(token, "106540352242955");
        _factory.Graph.Respond(_ => Json(HttpStatusCode.OK,
            """{"verified_name":"Acme Support","display_phone_number":"+1 555-0100","id":"106540352242955"}"""));

        var (status, body) = await PostTestAsync("whatsapp");

        status.Should().Be(HttpStatusCode.OK, body.GetRawText());
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("verifiedName").GetString().Should().Be("Acme Support");
        var request = _factory.Graph.Last();
        request.Authorization.Should().Be($"Bearer {token}", "the tenant's own token authenticates the call");
        request.Method.Should().Be("GET");
        request.Uri.Should().StartWith($"{GraphBaseUrl}/v21.0/106540352242955", "the configured base address and API version");
        body.GetRawText().Should().NotContain(token);
    }

    [Fact]
    public async Task TestConnection_ShouldReturnMetaError_WhenGraphAnswers401()
    {
        await SeedWhatsAppAsync($"EAArevoked{Guid.NewGuid():N}", "106540352242955");
        _factory.Graph.Respond(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Error validating access token: Session has expired.","type":"OAuthException","code":190}}"""));

        var (status, body) = await PostTestAsync("whatsapp");

        status.Should().Be(HttpStatusCode.OK, body.GetRawText());
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("code").GetString().Should().Be("provider-rejected");
        body.GetProperty("message").GetString().Should().Be("Error validating access token: Session has expired.");
    }

    [Fact]
    public async Task TestConnection_ShouldReportChannelNotConfigured_WhenAccessTokenMissing()
    {
        await SeedWhatsAppAsync(accessToken: null, "106540352242955");
        var before = _factory.Graph.Count;

        var (status, body) = await PostTestAsync("whatsapp");

        status.Should().Be(HttpStatusCode.OK, body.GetRawText());
        body.GetProperty("success").GetBoolean().Should().BeFalse();
        body.GetProperty("code").GetString().Should().Be("channel-not-configured");
        _factory.Graph.Count.Should().Be(before, "no outbound call without the tenant's token");
    }

    [Fact]
    public async Task TestConnection_ShouldReportNotTestable_WhenChannelIsWebChat()
    {
        var (status, body) = await PostTestAsync("webchat");

        status.Should().Be(HttpStatusCode.OK, body.GetRawText());
        body.GetProperty("success").GetBoolean().Should().BeFalse("a channel without a real test never reports success");
        body.GetProperty("code").GetString().Should().Be("not-testable");
    }

    [Fact]
    public async Task TestConnection_ShouldReturn400_WhenChannelUnknown()
    {
        var response = await _client.PostAsync("/api/v1/admin/channels/carrier-pigeon/test", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostTestAsync(string channel)
    {
        var response = await _client.PostAsync($"/api/v1/admin/channels/{channel}/test", null);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        return (response.StatusCode, json.RootElement.Clone());
    }

    private async Task SeedWhatsAppAsync(string? accessToken, string phoneNumberId)
    {
        var credentials = new Dictionary<string, string>
        {
            [WhatsAppCredentialKeys.PhoneNumberId] = phoneNumberId,
            [WhatsAppCredentialKeys.AppSecret] = "app-secret",
            [WhatsAppCredentialKeys.WebhookVerifyToken] = "verify",
        };
        if (accessToken is not null)
            credentials[WhatsAppCredentialKeys.AccessToken] = accessToken;

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig
            {
                TenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId),
                Channel = ChannelType.WhatsApp,
                IsActive = true,
                Credentials = credentials,
            },
            CancellationToken.None);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        public GraphStub Graph { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WhatsAppBaseUrl.ConfigurationKey, GraphBaseUrl);
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(WhatsAppConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Graph));
        }
    }

    public sealed record GraphRequest(string Method, string Uri, string? Authorization);

    /// <summary>Records every Graph request and answers with the current responder.</summary>
    public sealed class GraphStub : HttpMessageHandler
    {
        private readonly ConcurrentQueue<GraphRequest> _requests = new();
        private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.OK);

        public int Count => _requests.Count;

        public GraphRequest Last() => _requests.Last();

        public void Respond(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(new GraphRequest(
                request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, request.Headers.Authorization?.ToString()));
            return Task.FromResult(_respond(request));
        }
    }
}
