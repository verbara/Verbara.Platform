using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Dialer;
using Verbara.Sdk.Pro.Dialer.Contacts;
using Verbara.Sdk.Pro.Dialer.DependencyInjection;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;

// CA2012: NSubstitute records the ValueTask-returning store calls for `.Returns(...)` matching; the
// ValueTask is consumed by the extension, not awaited (as in StasisInboundConsumerTests).
#pragma warning disable CA2012

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// dialer-places-calls-end-to-end (design D1, D2, D9): Platform's mapping of its configuration onto the Pro dialer's
/// options. The engine's node must be the Api's Asterisk node (the cluster registers its only server under
/// <see cref="AsteriskNodeIds.Primary"/>), and the dialer's default trunk must be the one the Api's own calls fall
/// back to (<c>Asterisk:Outbound:DefaultTrunk</c>), so a campaign with no matching route reaches the carrier through
/// it in the measured <c>PJSIP/t-{id}/sip:{number}@{host}</c> form.
/// </summary>
public sealed class DialerHostExtensionsTests
{
    private const string Tenant = "acme";

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void ConfigureDialerOptions_ShouldUseTheApiAsteriskNode_WhenConfigured()
    {
        var options = new DialerOptions();

        DialerHostExtensions.ConfigureDialerOptions(options, Config(("Asterisk:Outbound:DefaultTrunk", "pstn")));

        options.NodeId.Should().Be(AsteriskNodeIds.Primary,
            "every activation originates on the node the cluster registers the Api's Asterisk server under");
    }

    [Fact]
    public async Task Originate_ShouldDialTheDefaultTrunkByName_WhenNoRouteMatches()
    {
        var configuration = Config(("Asterisk:Outbound:DefaultTrunk", "pstn"));

        // No outbound route matches, and the campaign has no default trunk.
        var routes = Substitute.For<OutboundRouteResolverBase>();
        routes.ResolveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Trunk?>((Trunk?)null));
        // The tenant stores an active trunk named like the host's default trunk.
        var trunks = Substitute.For<TrunkStoreBase>();
        trunks.GetByNameAsync("pstn", Tenant, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Trunk?>(new Trunk
            {
                Id = 7,
                Name = "pstn",
                IsActive = true,
                MatchHost = "203.0.113.10/32",
            }));

        var services = new ServiceCollection();
        services.AddSingleton<OutboundRouteResolverBase>(routes);
        services.AddSingleton<TrunkStoreBase>(trunks);
        services.AddProDialer(o => DialerHostExtensions.ConfigureDialerOptions(o, configuration));
        await using var provider = services.BuildServiceProvider();

        var builder = provider.GetRequiredService<OriginateBuilderBase>();
        var campaign = new Campaign { Id = 11, TenantId = Tenant, DefaultTrunkId = null };
        var contact = new ContactRecord(
            ContactId: 101, ContactListId: 1, CampaignId: 11, PhoneNumber: "+442071234567",
            ContactPhoneId: 201, AttemptNumber: 1, Priority: 0, AssignedAgentId: null, ExternalId: null);

        var result = await builder.TryBuildAsync(contact, campaign, CancellationToken.None);

        result.Status.Should().Be(OriginateBuildStatus.Built,
            "with no route and no campaign default trunk, the host's default trunk name is the tier that answers");
        result.Action!.Channel.Should().Be("PJSIP/t-7/sip:+442071234567@203.0.113.10");
    }
}
