using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real D2 — pins what <see cref="DigitalChannelsExtensions.AddDigitalChannels"/>
/// exposes: WhatsApp (handler + connector) and WebChat (connector) and nothing else, plus the Twilio
/// <see cref="ISmsProvider"/> moved out of Program.cs, which stays conditional on <c>Twilio:AccountSid</c>.
/// Registration-level, on a bare <see cref="ServiceCollection"/> — no host.
/// </summary>
public sealed class DigitalChannelsRegistrarTests
{
    private static ServiceProvider Build(IDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITenantChannelConfigStore>());
        services.AddDigitalChannels(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddDigitalChannels_ShouldRegisterOnlyWhatsAppAndWebChat_WhenComposed()
    {
        using var provider = Build();

        provider.GetServices<IWebhookHandler>().Select(h => h.Channel)
            .Should().Equal(ChannelType.WhatsApp);
        provider.GetServices<IChannelConnector>().Select(c => c.Channel)
            .Should().BeEquivalentTo([ChannelType.WebChat, ChannelType.WhatsApp]);
    }

    [Fact]
    public void AddDigitalChannels_ShouldRegisterTwilioProvider_WhenAccountSidConfigured()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["Twilio:AccountSid"] = "ACgate",
            ["Twilio:AuthToken"] = "token",
        });

        provider.GetService<ISmsProvider>().Should().NotBeNull();
        provider.GetServices<IWebhookHandler>().Should().NotContain(h => h.Channel == ChannelType.Sms);
        provider.GetServices<IChannelConnector>().Should().NotContain(c => c.Channel == ChannelType.Sms);
    }

    [Fact]
    public void AddDigitalChannels_ShouldNotRegisterTwilioProvider_WhenAccountSidMissing()
    {
        using var provider = Build();

        provider.GetService<ISmsProvider>().Should().BeNull();
    }

    // ── WhatsApp:BaseUrl (operator decision DQ13; spec channel-credential-config) ──────────────────
    // Read explicitly at registration (no reflection options binding, AOT). https only, except plain http
    // to a loopback host: the tenant's bearer token travels to this address.

    [Fact]
    public void AddDigitalChannels_ShouldKeepDefaultWhatsAppBaseUrl_WhenNotConfigured()
    {
        using var provider = Build();

        provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.BaseUrl.Should().Be("https://graph.facebook.com");
    }

    [Theory]
    [InlineData("https://graph-stub.lab.internal", "https://graph-stub.lab.internal")]
    [InlineData("https://graph-stub.lab.internal/", "https://graph-stub.lab.internal")]
    [InlineData("https://proxy.example.com/meta", "https://proxy.example.com/meta")]
    [InlineData("http://127.0.0.1:9100", "http://127.0.0.1:9100")]
    [InlineData("http://localhost:9100/", "http://localhost:9100")]
    [InlineData("http://[::1]:9100", "http://[::1]:9100")]
    public void AddDigitalChannels_ShouldUseConfiguredWhatsAppBaseUrl_WhenValid(string configured, string expected)
    {
        using var provider = Build(new Dictionary<string, string?> { ["WhatsApp:BaseUrl"] = configured });

        provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.BaseUrl.Should().Be(expected);
    }

    [Theory]
    [InlineData("http://graph-stub.lab.internal")]
    [InlineData("http://10.0.0.5:9100")]
    [InlineData("graph.facebook.com")]
    [InlineData("/relative/path")]
    [InlineData("ftp://graph.facebook.com")]
    [InlineData("https://graph.facebook.com/?token=x")]
    [InlineData("https://graph.facebook.com/#frag")]
    [InlineData("https://user:pass@graph.facebook.com")]
    public void AddDigitalChannels_ShouldThrowNamingKey_WhenWhatsAppBaseUrlInvalid(string configured)
    {
        var act = () => Build(new Dictionary<string, string?> { ["WhatsApp:BaseUrl"] = configured }).Dispose();

        act.Should().Throw<InvalidOperationException>().WithMessage("*WhatsApp:BaseUrl*");
    }
}
