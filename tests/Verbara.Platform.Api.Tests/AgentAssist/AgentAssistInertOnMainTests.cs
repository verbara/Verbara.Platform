using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.AgentAssist.Options;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// Pro <c>2.28.0-pro</c>: without <see cref="AgentAssistOptions.AudioSocketAdvertisedAddress"/> the Agent Assist engine
/// creates no snoop (it counts the skip and warns once), and recognizers resolve per session, so the
/// <c>Func&lt;SpeechRecognizer&gt;</c> singleton is gone. The Api registers the engine (<c>AddProAgentAssist</c>, with an
/// Analytics connection string) without <c>WithSnoop</c>, so the engine stays inert: this pins that the real service
/// collection sets no address and registers no recognizer. The no-snoop path itself is Pro's, pinned by its engine
/// tests; a host change that configures the snoop supersedes this pin.
/// </summary>
public sealed class AgentAssistInertOnMainTests : IAsyncLifetime
{
    private IContainer? _postgres;

    [Fact]
    public void Boot_ShouldSetNoAdvertisedAddressAndRegisterNoRecognizer_WhenTheApiRegistersAgentAssist()
    {
        using var factory = new AnalyticsHostFactory(ConnectionString);

        var options = factory.Services.GetRequiredService<AgentAssistOptions>();

        factory.Registrations.Should().Contain(d => d.ServiceType == typeof(AgentAssistSupervisor),
            "precondition: with an Analytics connection string the Api registers Agent Assist (AddProAgentAssist)");
        options.AudioSocketAdvertisedAddress.Should().BeNull(
            "the Api configures no snoop, so the engine creates none");
        factory.Registrations.Should().NotContain(
            d => d.ServiceType == typeof(SpeechRecognizer) || d.ServiceType == typeof(Func<SpeechRecognizer>),
            "the Api registers no recognizer, so no session setup reaches recognizer resolution");
    }

    private string ConnectionString =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres;Application Name=agent-assist-inert";

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only temporary
            // server, so a socket probe greens before 5432 accepts connections.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <summary>
    /// The real Api <c>Program</c> over an Analytics connection string, with the Verbara hosted services stripped, AMI
    /// and the server stubbed and the licensing stubs. <see cref="Registrations"/> is the service collection as
    /// <c>Program.cs</c> left it, before any stub ran.
    /// </summary>
    private sealed class AnalyticsHostFactory(string analyticsConnectionString) : WebApplicationFactory<Program>
    {
        public IReadOnlyList<ServiceDescriptor> Registrations { get; private set; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("ConnectionStrings:Analytics", analyticsConnectionString);

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                Registrations = services.ToList();

                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
            });
            return base.CreateHost(builder);
        }
    }
}
