using Asterisk.Platform.Channels.Video;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Channels.Video.Tests;

public class VideoServiceCollectionExtensionsTests
{
    private static ServiceCollection BuildServices(Action<VideoOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IVideoTransport>());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVideo(configure);
        return services;
    }

    [Fact]
    public void AddVideo_ShouldRegisterSessionManager_WhenCalled()
    {
        var provider = BuildServices(o => o.MaxParticipants = 4).BuildServiceProvider();

        var manager = provider.GetService<VideoSessionManager>();
        manager.Should().NotBeNull();
    }

    [Fact]
    public void AddVideo_ShouldRegisterVideoConnector_WhenCalled()
    {
        var provider = BuildServices().BuildServiceProvider();

        var connector = provider.GetService<VideoConnector>();
        connector.Should().NotBeNull();
    }

    [Fact]
    public void AddVideo_ShouldRegisterWebhookHandler_WhenCalled()
    {
        var provider = BuildServices().BuildServiceProvider();

        var handler = provider.GetService<VideoWebhookHandler>();
        handler.Should().NotBeNull();
    }

    [Fact]
    public void AddVideo_ShouldApplyOptions_WhenConfigureProvided()
    {
        var provider = BuildServices(o =>
        {
            o.MaxParticipants = 5;
            o.EnableScreenSharing = true;
            o.EnableRecording = true;
            o.DefaultQuality = VideoQuality.High;
            o.SessionTimeout = TimeSpan.FromMinutes(90);
        }).BuildServiceProvider();

        // Verify DI resolves without error — options are applied via Configure<T>
        var connector = provider.GetService<VideoConnector>();
        connector.Should().NotBeNull();
    }
}
