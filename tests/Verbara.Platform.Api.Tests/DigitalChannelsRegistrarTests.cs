using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Sms;
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
}
