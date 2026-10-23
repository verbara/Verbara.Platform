using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Pro.Cluster;

namespace Verbara.Platform.Api.Tests.Voice;

/// <summary>
/// stasis-app-has-one-registrant (spec <c>voice-stasis-app-ownership</c>, Platform/ADR-0044): the per-node ARI
/// client of the cluster's Asterisk node registers a Stasis app of its own, so only the inbound-voice leader's
/// consumer registers the dialplan's app (<c>Asterisk:Ari:Application</c>). Every test boots the real
/// <c>Program</c> under <c>Testing</c> with <c>Asterisk:Ami:Hostname</c> set (the node is composed only then),
/// in the <see cref="Health.ReadinessScopeTests"/> shape.
/// </summary>
public sealed class StasisAppNamesCompositionTests
{
    private const string AmiHostname = "ami.invalid";
    private const string AriBaseUrl = "http://ari.invalid:8088";

    private sealed class Factory(IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Asterisk:Ami:Hostname", AmiHostname);
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
            });
            return base.CreateHost(builder);
        }

        internal ClusterNodeOptions PrimaryNode =>
            Services.GetRequiredService<IOptions<ClusterOptions>>().Value.InitialNodes["primary"];
    }

    [Fact]
    public void Boot_ShouldGiveTheNodeClientItsOwnStasisApp_WhenOnlyTheApplicationIsSet()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Asterisk:Ari:BaseUrl"] = AriBaseUrl,
            ["Asterisk:Ari:Application"] = "verbara",
        });

        factory.PrimaryNode.Ari!.Application.Should().Be("verbara-node");
    }

    [Fact]
    public void Boot_ShouldUseTheExplicitNodeApplication_WhenOneIsSet()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Asterisk:Ari:BaseUrl"] = AriBaseUrl,
            ["Asterisk:Ari:Application"] = "acme",
            ["Asterisk:Ari:NodeApplication"] = "acme-cluster",
        });

        factory.PrimaryNode.Ari!.Application.Should().Be("acme-cluster");
    }

    [Fact]
    public void Boot_ShouldFailNamingBothSettings_WhenTheNodeApplicationEqualsTheApplication()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Asterisk:Ari:BaseUrl"] = AriBaseUrl,
            ["Asterisk:Ari:Application"] = "verbara",
            ["Asterisk:Ari:NodeApplication"] = "Verbara",
        });

        var boot = () => factory.Services;

        var thrown = boot.Should().Throw<Exception>().Which;
        var messages = string.Join(" | ", Chain(thrown).Select(e => e.Message));
        messages.Should().Contain("Asterisk:Ari:Application").And.Contain("Asterisk:Ari:NodeApplication");
    }

    [Fact]
    public void Boot_ShouldNotCheckTheNames_WhenAriIsNotConfigured()
    {
        using var factory = new Factory(new Dictionary<string, string?>
        {
            ["Asterisk:Ari:Application"] = "verbara",
            ["Asterisk:Ari:NodeApplication"] = "verbara",
        });

        factory.PrimaryNode.Ari.Should().BeNull("without Asterisk:Ari:BaseUrl the node has no ARI client");
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            yield return e;
    }
}
