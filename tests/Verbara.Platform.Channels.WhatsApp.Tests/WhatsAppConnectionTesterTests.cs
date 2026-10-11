using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

/// <summary>
/// <see cref="WhatsAppConnectionTester"/> as <c>AddWhatsApp</c> composes it (whatsapp-works-for-real D15):
/// a Graph API phone-number lookup with the tenant's own token, Meta's verdict reported verbatim, and no
/// request at all without a token or phone number id.
/// </summary>
public class WhatsAppConnectionTesterTests
{
    private const string GraphBaseUrl = "https://graph.example.test";
    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    public async Task TestConnection_ShouldReturnVerifiedName_WhenGraphAnswers200()
    {
        var http = new RecordingGraphHandler(_ => Json(HttpStatusCode.OK, """{"id":"pn-a","verified_name":"Acme"}"""));
        var tester = Build(Config(new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" }), http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.VerifiedName.Should().Be("Acme");
        result.Code.Should().BeNull();
        http.Requests.Should().ContainSingle();
        http.Requests[0].Authorization.Should().Be("Bearer token-a");
        http.Requests[0].Uri.Should().Be($"{GraphBaseUrl}/v21.0/pn-a?fields=verified_name,display_phone_number");
    }

    [Fact]
    public async Task TestConnection_ShouldReturnMetaError_WhenGraphAnswers401()
    {
        var http = new RecordingGraphHandler(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":190}}"""));
        var tester = Build(Config(new() { ["AccessToken"] = "revoked", ["PhoneNumberId"] = "pn-a" }), http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ChannelConnectionTestCodes.ProviderRejected);
        result.Message.Should().Be("Invalid OAuth access token.");
    }

    [Fact]
    public async Task TestConnection_ShouldReportStatus_WhenErrorBodyIsNotJson()
    {
        var http = new RecordingGraphHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>bad gateway</html>", Encoding.UTF8, "text/html"),
        });
        var tester = Build(Config(new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" }), http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ChannelConnectionTestCodes.ProviderRejected);
        result.Message.Should().Contain("502");
    }

    [Theory]
    [InlineData("AccessToken")]
    [InlineData("PhoneNumberId")]
    public async Task TestConnection_ShouldReturnChannelNotConfigured_WhenKeyMissing(string missing)
    {
        var credentials = new Dictionary<string, string> { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" };
        credentials.Remove(missing);
        var http = new RecordingGraphHandler();
        var tester = Build(Config(credentials), http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ChannelConnectionTestCodes.ChannelNotConfigured);
        result.Message.Should().Contain(missing);
        http.Requests.Should().BeEmpty("no request may reach Meta without the tenant's token and number");
    }

    [Fact]
    public async Task TestConnection_ShouldReturnChannelNotConfigured_WhenTenantHasNoConfig()
    {
        var http = new RecordingGraphHandler();
        var tester = Build(config: null, http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Code.Should().Be(ChannelConnectionTestCodes.ChannelNotConfigured);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnection_ShouldReturnProviderUnreachable_WhenRequestFails()
    {
        var http = new RecordingGraphHandler(_ => throw new HttpRequestException("connection refused"));
        var tester = Build(Config(new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn-a" }), http);

        var result = await tester.TestAsync(Tenant, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ChannelConnectionTestCodes.ProviderUnreachable);
        result.Message.Should().NotContain("connection refused", "an exception message is not echoed");
    }

    [Fact]
    public async Task TestConnection_ShouldUseTenantApiVersionAndEscapeSegments_WhenConfigured()
    {
        var http = new RecordingGraphHandler(_ => Json(HttpStatusCode.OK, """{"verified_name":"Acme"}"""));
        var tester = Build(
            Config(new() { ["AccessToken"] = "token-a", ["PhoneNumberId"] = "pn/../x", ["ApiVersion"] = "v22.0" }), http);

        await tester.TestAsync(Tenant, CancellationToken.None);

        http.Requests[0].Uri.Should().StartWith($"{GraphBaseUrl}/v22.0/pn%2F..%2Fx?");
    }

    [Fact]
    public void AddWhatsApp_ShouldRegisterConnectionTester_WhenWhatsAppIsAdded()
    {
        using var provider = Provider(Substitute.For<ITenantChannelConfigStore>(), new RecordingGraphHandler());

        provider.GetServices<IChannelConnectionTester>().Should().ContainSingle()
            .Which.Channel.Should().Be(ChannelType.WhatsApp);
    }

    private static IChannelConnectionTester Build(TenantChannelConfig? config, RecordingGraphHandler http)
    {
        var store = Substitute.For<ITenantChannelConfigStore>();
        store.GetAsync(Tenant, ChannelType.WhatsApp, Arg.Any<CancellationToken>()).Returns(config);
        // The provider is deliberately not disposed here: the tester it hands out is used after this returns.
        var provider = Provider(store, http);
        return provider.GetServices<IChannelConnectionTester>().Single();
    }

    private static ServiceProvider Provider(ITenantChannelConfigStore store, RecordingGraphHandler http)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddWhatsApp(o => o.BaseUrl = GraphBaseUrl);
        services.AddHttpClient(WhatsAppConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => http);
        return services.BuildServiceProvider();
    }

    private static TenantChannelConfig Config(Dictionary<string, string> credentials) => new()
    {
        TenantId = Tenant,
        Channel = ChannelType.WhatsApp,
        Credentials = credentials,
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
