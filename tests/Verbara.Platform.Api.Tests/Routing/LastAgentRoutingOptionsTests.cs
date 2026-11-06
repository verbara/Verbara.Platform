using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Routing.Inbound;

namespace Verbara.Platform.Api.Tests.Routing;

/// <summary>
/// conversations-return-to-last-agent task 1.2 (design D1): <c>InboundRouting:LastAgent:Enabled</c> (default off) and
/// <c>InboundRouting:LastAgent:WindowHours</c> (default 72, valid 1 to 720), bound without the reflection binder, and a
/// start that fails naming the key. The key names are the config fixture's, verbatim. The host cases start the real
/// <c>Program</c>.
/// </summary>
public sealed class LastAgentRoutingOptionsTests
{
    [Fact]
    public void LastAgentRoutingOptions_ShouldDefaultToOffAnd72Hours_WhenNothingIsConfigured()
    {
        var options = new LastAgentRoutingOptions();

        options.Enabled.Should().BeFalse();
        options.WindowHours.Should().Be(72);
    }

    [Fact]
    public void Keys_ShouldBeTheFixtureNames_WhenADeployerWritesThem()
    {
        LastAgentRoutingOptionsSetup.SectionName.Should().Be("InboundRouting:LastAgent");
        LastAgentRoutingOptionsSetup.EnabledKey.Should().Be("InboundRouting:LastAgent:Enabled");
        LastAgentRoutingOptionsSetup.WindowHoursKey.Should().Be("InboundRouting:LastAgent:WindowHours");
        LastAgentRoutingOptions.MinWindowHours.Should().Be(1);
        LastAgentRoutingOptions.MaxWindowHours.Should().Be(720);
    }

    [Fact]
    public void Bind_ShouldReadBothKeys_WhenTheSectionSetsThem()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InboundRouting:LastAgent:Enabled"] = "true",
            ["InboundRouting:LastAgent:WindowHours"] = "720",
        }).Build();
        var options = new LastAgentRoutingOptions();

        LastAgentRoutingOptionsSetup.Bind(configuration.GetSection(LastAgentRoutingOptionsSetup.SectionName), options);

        options.Enabled.Should().BeTrue();
        options.WindowHours.Should().Be(720);
    }

    [Fact]
    public void Host_ShouldResolveOffAnd72Hours_WhenTheSectionIsAbsent()
    {
        using var factory = Factory(new Dictionary<string, string?>());

        var options = factory.Services.GetRequiredService<IOptions<LastAgentRoutingOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.WindowHours.Should().Be(72);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("720")]
    public void Host_ShouldResolveTheBoundValues_WhenTheWindowIsAtABound(string hours)
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["InboundRouting:LastAgent:Enabled"] = "true",
            ["InboundRouting:LastAgent:WindowHours"] = hours,
        });

        var options = factory.Services.GetRequiredService<IOptions<LastAgentRoutingOptions>>().Value;

        options.Enabled.Should().BeTrue();
        options.WindowHours.Should().Be(int.Parse(hours, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    [InlineData("-1")]
    public void Host_ShouldFailStartupNamingTheKey_WhenTheWindowIsOutOfRange(string hours)
    {
        using var factory = Factory(new Dictionary<string, string?> { ["InboundRouting:LastAgent:WindowHours"] = hours });

        var start = () => factory.Services;

        start.Should().Throw<Exception>()
            .Where(e => Flatten(e).Contains("InboundRouting:LastAgent:WindowHours", StringComparison.Ordinal),
                "the startup failure names the key so the deployer knows what to fix");
    }

    [Theory]
    [InlineData("InboundRouting:LastAgent:Enabled", "yes")]
    [InlineData("InboundRouting:LastAgent:WindowHours", "72h")]
    public void Host_ShouldFailStartupNamingTheKeyAndValue_WhenAValueIsUnparsable(string key, string value)
    {
        using var factory = Factory(new Dictionary<string, string?> { [key] = value });

        var start = () => factory.Services;

        start.Should().Throw<Exception>()
            .Where(e => Flatten(e).Contains(key, StringComparison.Ordinal)
                        && Flatten(e).Contains($"'{value}'", StringComparison.Ordinal),
                "a typo must not silently keep the default");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        IReadOnlyDictionary<string, string?> settings) =>
        new PlatformApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });

    private static string Flatten(Exception e)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = e; current is not null; current = current.InnerException)
            text.AppendLine(current.Message);
        if (e is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                text.AppendLine(Flatten(inner));
        return text.ToString();
    }
}
