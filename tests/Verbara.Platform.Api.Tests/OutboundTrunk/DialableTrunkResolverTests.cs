using Verbara.Platform.Api.Services.OutboundTrunk;
using Verbara.Platform.Core;
using static Verbara.Platform.Api.Tests.OutboundTrunk.TrunkFakes;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D1, D2): the tenant's own outbound routes in ascending priority, each route's
/// trunk loaded from the tenant's stored trunks, then the default trunk by name; the result is the endpoint
/// <c>t-{id}</c> and a dial host, or <c>NoTrunk</c>, or <c>NotDialable(id)</c>.
/// </summary>
public sealed class DialableTrunkResolverTests
{
    private const string Acme = "acme";
    private const string Beta = "beta";
    private const string Number = "+573001234567";
    private static readonly TenantId AcmeId = new(Acme);

    private readonly FakeRouteStore _routes = new();
    private readonly FakeTrunkStore _trunks = new();

    private Task<DialableTrunkResolution> ResolveAsync(string? defaultTrunk = null, bool withRoutes = true, string number = Number) =>
        Resolver(withRoutes ? _routes : null, _trunks, defaultTrunk).ResolveAsync(AcmeId, number, CancellationToken.None).AsTask();

    private static void ShouldDial(DialableTrunkResolution result, long trunkId, string host = RouteHost)
    {
        result.Kind.Should().Be(DialableTrunkResolutionKind.Dialable);
        result.Target!.TrunkId.Should().Be(trunkId);
        result.Target.EndpointId.Should().Be($"t-{trunkId}");
        result.Target.Host.Should().Be(host);
    }

    [Fact]
    public async Task ResolveAsync_ShouldDialTheRouteTrunk_LoadedFromTheTenantsStoredTrunks()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 1));
        _trunks.Add(Acme, IpTrunk(1, "premium-sip")).Add(Acme, IpTrunk(7, "pstn", "198.51.100.7"));

        var result = await ResolveAsync(defaultTrunk: "pstn");

        ShouldDial(result, 1);
        _routes.Asked.Should().Equal(Acme);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReadOnlyTheTenantsRoutes_WhenListingThemByPriority()
    {
        _routes.Add(Beta, Route(1, "+57", trunkId: 2, priority: 0)).Add(Acme, Route(2, "+57", trunkId: 3, priority: 5));
        _trunks.Add(Beta, IpTrunk(2, "beta-pstn", "192.0.2.2")).Add(Acme, IpTrunk(3, "acme-pstn"));

        var result = await ResolveAsync();

        ShouldDial(result, 3);
        result.TrunkId.Should().Be(3);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPickTheLowestPriorityNumber_ThenTheLowestId()
    {
        _routes.Add(Acme, Route(9, "+57", trunkId: 4, priority: 10))
            .Add(Acme, Route(8, "+57", trunkId: 5, priority: 0))
            .Add(Acme, Route(3, "+57", trunkId: 3, priority: 0));
        _trunks.Add(Acme, IpTrunk(3, "a", "192.0.2.3")).Add(Acme, IpTrunk(4, "b", "192.0.2.4")).Add(Acme, IpTrunk(5, "c", "192.0.2.5"));

        var result = await ResolveAsync();

        ShouldDial(result, 3, "192.0.2.3");
    }

    [Fact]
    public async Task ResolveAsync_ShouldMoveToTheNextRoute_WhenTheRouteTrunkIsNotStoredForTheTenant()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 9, priority: 0)).Add(Acme, Route(2, "+57", trunkId: 3, priority: 1));
        _trunks.Add(Beta, IpTrunk(9, "beta-only", "192.0.2.9")).Add(Acme, IpTrunk(3, "acme-pstn"));

        var result = await ResolveAsync();

        ShouldDial(result, 3);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallBackToTheDefault_WhenNoRouteTrunkIsStoredForTheTenant()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 9));
        _trunks.Add(Beta, IpTrunk(9, "beta-only", "192.0.2.9")).Add(Acme, IpTrunk(7, "pstn", "198.51.100.7"));

        var result = await ResolveAsync(defaultTrunk: "pstn");

        ShouldDial(result, 7, "198.51.100.7");
        result.TrunkId.Should().NotBe(9);
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipAnInactiveRouteTrunk()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 1, priority: 0)).Add(Acme, Route(2, "+57", trunkId: 3, priority: 1));
        _trunks.Add(Acme, IpTrunk(1, "off", active: false)).Add(Acme, IpTrunk(3, "on", "192.0.2.3"));

        ShouldDial(await ResolveAsync(), 3, "192.0.2.3");
    }

    [Fact]
    public async Task ResolveAsync_ShouldSkipCampaignRoutesAndPatternsThatAreNotAPrefix()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 1, priority: 0, campaignId: 42))
            .Add(Acme, Route(2, "+44", trunkId: 2, priority: 1))
            .Add(Acme, Route(3, "300", trunkId: 2, priority: 2));
        _trunks.Add(Acme, IpTrunk(1, "campaign")).Add(Acme, IpTrunk(2, "uk")).Add(Acme, IpTrunk(7, "pstn", "198.51.100.7"));

        ShouldDial(await ResolveAsync(defaultTrunk: "pstn"), 7, "198.51.100.7");
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallThroughToTheDefault_WhenTheTrunkReloadThrows()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 1));
        _trunks.Add(Acme, IpTrunk(7, "pstn", "198.51.100.7"));
        _trunks.ThrowOnGet = new NotSupportedException();

        ShouldDial(await ResolveAsync(defaultTrunk: "pstn"), 7, "198.51.100.7");
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseTheDefaultByName_WhenTheTenantHasNoRoutes()
    {
        _trunks.Add(Acme, IpTrunk(7, "pstn", "203.0.113.10/32"));

        ShouldDial(await ResolveAsync(defaultTrunk: "pstn"), 7);
    }

    [Fact]
    public async Task ResolveAsync_ShouldFallThroughToTheDefault_WhenTheRouteStoreThrows()
    {
        _routes.Throw = new InvalidOperationException("dialer database down");
        _trunks.Add(Acme, IpTrunk(7, "pstn"));

        ShouldDial(await ResolveAsync(defaultTrunk: "pstn"), 7);
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseTheDefault_WhenNoRouteStoreIsRegistered()
    {
        _trunks.Add(Acme, IpTrunk(7, "pstn"));

        ShouldDial(await ResolveAsync(defaultTrunk: "pstn", withRoutes: false), 7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("absent")]
    [InlineData("t-1")]
    public async Task ResolveAsync_ShouldAnswerNoTrunk_WhenTheDefaultNamesNoStoredTrunk(string? defaultTrunk)
    {
        _trunks.Add(Acme, IpTrunk(1, "pstn"));

        (await ResolveAsync(defaultTrunk)).Kind.Should().Be(DialableTrunkResolutionKind.NoTrunk);
    }

    [Fact]
    public async Task ResolveAsync_ShouldAnswerNoTrunk_WhenTheDefaultNameIsNotStoredForTheTenant()
    {
        _trunks.Add(Beta, IpTrunk(2, "pstn"));

        (await ResolveAsync(defaultTrunk: "pstn")).Kind.Should().Be(DialableTrunkResolutionKind.NoTrunk);
    }

    [Fact]
    public async Task ResolveAsync_ShouldAnswerNoTrunk_WhenTheTrunkStoreCannotLookUpByName()
    {
        _trunks.ThrowOnGetByName = new NotSupportedException();

        (await ResolveAsync(defaultTrunk: "pstn")).Kind.Should().Be(DialableTrunkResolutionKind.NoTrunk);
    }

    [Fact]
    public async Task ResolveAsync_ShouldAnswerNoTrunk_WhenTheDefaultIsInactive()
    {
        _trunks.Add(Acme, IpTrunk(7, "pstn", active: false));

        (await ResolveAsync(defaultTrunk: "pstn")).Kind.Should().Be(DialableTrunkResolutionKind.NoTrunk);
    }

    [Fact]
    public async Task ResolveAsync_ShouldAnswerNotDialable_WhenTheTrunkHasNoHost()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 4));
        _trunks.Add(Acme, AuthOnlyTrunk(4, "auth-only"));

        var result = await ResolveAsync();

        result.Kind.Should().Be(DialableTrunkResolutionKind.NotDialable);
        result.TrunkId.Should().Be(4);
        result.Target.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_ShouldAnswerNotDialable_WhenTheDefaultMatchHostIsARange()
    {
        _trunks.Add(Acme, IpTrunk(5, "pstn", "203.0.113.0/24"));

        var result = await ResolveAsync(defaultTrunk: "pstn");

        result.Kind.Should().Be(DialableTrunkResolutionKind.NotDialable);
        result.TrunkId.Should().Be(5);
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation()
    {
        _routes.Add(Acme, Route(1, "+57", trunkId: 1));
        _trunks.Add(Acme, IpTrunk(1, "pstn"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Resolver(_routes, _trunks, "pstn").ResolveAsync(AcmeId, Number, cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Channel_ShouldSpellTheEndpointIdAndTheSipUriToTheHost()
    {
        var target = new DialableTrunkTarget(1, "t-1", RouteHost);

        target.Channel("+573001234567").Should().Be("PJSIP/t-1/sip:+573001234567@203.0.113.10");
    }
}
