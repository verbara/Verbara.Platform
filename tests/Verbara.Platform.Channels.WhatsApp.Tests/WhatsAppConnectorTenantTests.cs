using System.Net;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

/// <summary>
/// The WhatsApp connector as the host composes it (<c>AddWhatsApp</c> + DI): credentials come from
/// the sending tenant's channel configuration at send time, never from process-wide options, and a
/// template goes out only when the caller names one.
/// </summary>
public class WhatsAppConnectorTenantTests
{
    private const string GraphBaseUrl = "https://graph.example.test";

    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    [Fact]
    public async Task SendAsync_ShouldUseTenantTokenAndPhoneNumberId_WhenConfigured()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" });
        SeedConfig(store, TenantB, new() { ["AccessToken"] = "token-b", ["PhoneNumberId"] = "pn-b" });
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var resultA = await connector.SendAsync(Text(TenantA, "15550000001", "hello A"), CancellationToken.None);
        var resultB = await connector.SendAsync(Text(TenantB, "15550000002", "hello B"), CancellationToken.None);

        resultA.Success.Should().BeTrue();
        resultB.Success.Should().BeTrue();
        http.Requests.Should().HaveCount(2);
        http.Requests[0].Authorization.Should().Be("Bearer token-a");
        http.Requests[0].Uri.Should().Be($"{GraphBaseUrl}/v21.0/pn-a/messages");
        http.Requests[1].Authorization.Should().Be("Bearer token-b");
        http.Requests[1].Uri.Should().Be($"{GraphBaseUrl}/v21.0/pn-b/messages");
    }

    [Fact]
    public async Task SendAsync_ShouldSendText_WhenNoTemplateIdEvenWithoutInboundHistory()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" });
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000003", "Hi there!"), CancellationToken.None);

        result.Success.Should().BeTrue();
        http.Requests.Should().ContainSingle();
        http.Requests[0].Body.Should().Contain("\"type\":\"text\"");
        http.Requests[0].Body.Should().Contain("\"body\":\"Hi there!\"");
        http.Requests[0].Body.Should().NotContain("template");
    }

    [Fact]
    public async Task SendAsync_ShouldSendTemplate_WhenTemplateIdIsSet()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" });
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(
            Text(TenantA, "15550000004", "ignored", templateId: "order_update"), CancellationToken.None);

        result.Success.Should().BeTrue();
        http.Requests[0].Body.Should().Contain("\"type\":\"template\"");
        http.Requests[0].Body.Should().Contain("\"name\":\"order_update\"");
    }

    [Fact]
    public async Task SendAsync_ShouldFail_WhenTenantHasNoAccessToken()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["PhoneNumberId"] = "pn-a" });
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000005", "hello"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("channel-not-configured");
        http.Requests.Should().BeEmpty("no request may reach the provider without the tenant's token");
    }

    [Fact]
    public async Task SendAsync_ShouldFail_WhenTenantHasNoPhoneNumberId()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a" });
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000006", "hello"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("channel-not-configured");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldFail_WhenTenantHasNoWhatsAppConfig()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000007", "hello"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("channel-not-configured");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldFail_WhenTenantWhatsAppConfigIsInactive()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" }, isActive: false);
        var http = new RecordingGraphHandler();
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000008", "hello"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("channel-not-configured");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldPreserveProviderErrorCode_WhenGraphAnswers401()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "revoked", ["PhoneNumberId"] = "pn-a" });
        var http = new RecordingGraphHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":190}}""",
                Encoding.UTF8, "application/json"),
        });
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000009", "hello"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
    }

    [Fact]
    public async Task AddWhatsApp_ShouldAttachResiliencePolicy_ToNamedClient()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" });
        var attempts = 0;
        var http = new RecordingGraphHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException("transient network blip");
            return RecordingGraphHandler.Accepted();
        });
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000010", "hello"), CancellationToken.None);

        result.Success.Should().BeTrue("the keyed channel.whatsapp policy retries the transient failure");
        attempts.Should().Be(2);
    }

    [Fact]
    public void AddWhatsApp_ShouldResolveConnectorAsSingletonChannelConnector_WhenRegistered()
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        using var provider = BuildProvider(store, new RecordingGraphHandler());

        var first = provider.GetRequiredService<WhatsAppConnector>();
        var second = provider.GetRequiredService<WhatsAppConnector>();
        var exposed = provider.GetServices<IChannelConnector>();

        second.Should().BeSameAs(first, "a singleton registry must not pin a transient typed HttpClient");
        exposed.Should().ContainSingle(c => c.Channel == ChannelType.WhatsApp)
            .Which.Should().BeSameAs(first);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task SendAsync_ShouldDisposeProviderResponse_WhenSendCompletes(HttpStatusCode status)
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        SeedConfig(store, TenantA, new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" });
        var responses = new List<TrackedResponse>();
        var http = new RecordingGraphHandler(_ =>
        {
            var response = new TrackedResponse(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK
                        ? """{"messaging_product":"whatsapp","messages":[{"id":"wamid.disposed"}]}"""
                        : """{"error":{"message":"Invalid OAuth access token","code":190}}""",
                    Encoding.UTF8, "application/json"),
            };
            responses.Add(response);
            return response;
        });
        using var provider = BuildProvider(store, http);
        var connector = provider.GetRequiredService<WhatsAppConnector>();

        var result = await connector.SendAsync(Text(TenantA, "15550000009", "hi"), CancellationToken.None);

        result.Success.Should().Be(status == HttpStatusCode.OK);
        responses.Should().ContainSingle().Which.Disposed.Should().BeTrue("the connector owns the provider response");
    }

    private sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static ServiceProvider BuildProvider(ITenantChannelConfigStore store, RecordingGraphHandler http)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddWhatsApp(o => o.BaseUrl = GraphBaseUrl);
        services.AddHttpClient(nameof(WhatsAppConnector)).ConfigurePrimaryHttpMessageHandler(() => http);
        return services.BuildServiceProvider();
    }

    private static void SeedConfig(
        ITenantChannelConfigStore store,
        TenantId tenant,
        Dictionary<string, string> credentials,
        bool isActive = true)
    {
        store.GetAsync(tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new TenantChannelConfig
            {
                TenantId = tenant,
                Channel = ChannelType.WhatsApp,
                Credentials = credentials,
                IsActive = isActive,
            });
    }

    private static OutboundMessage Text(TenantId tenant, string to, string text, string? templateId = null) =>
        new(
            new ChannelAddress(ChannelType.WhatsApp, to),
            new MessageEnvelope([new TextBlock(text)]),
            tenant,
            EntityId.From("conv-" + to),
            templateId);
}

/// <summary>Stub Graph API: records every request the connector sends and answers per a callback.</summary>
internal sealed class RecordingGraphHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond = respond ?? (_ => Accepted());

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Accepted() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """{"messaging_product":"whatsapp","messages":[{"id":"wamid.stub001"}]}""",
            Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = _respond(request);
        Requests.Add(new RecordedRequest(
            request.RequestUri?.ToString(),
            request.Headers.Authorization?.ToString(),
            body));
        return response;
    }
}

internal sealed record RecordedRequest(string? Uri, string? Authorization, string Body);
