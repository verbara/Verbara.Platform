using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Services.OutboundTrunk;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;

namespace Verbara.Platform.Api.Tests.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D8): the two real seams of <see cref="DialableTrunkResolver"/>. Routes are
/// listed per tenant; trunks are stored per tenant, so a host can only come from the tenant's own stored trunk.
/// </summary>
internal sealed class FakeRouteStore : OutboundRouteStoreBase
{
    public Dictionary<string, List<OutboundRoute>> Routes { get; } = [];
    public Exception? Throw { get; set; }
    public List<string> Asked { get; } = [];

    public FakeRouteStore Add(string tenantId, OutboundRoute route)
    {
        if (!Routes.TryGetValue(tenantId, out var list))
            Routes[tenantId] = list = [];
        list.Add(route);
        return this;
    }

    public override ValueTask<IReadOnlyList<OutboundRoute>> ListByPriorityAsync(string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Asked.Add(tenantId);
        if (Throw is not null)
            throw Throw;
        // Deliberately NOT sorted: the resolver must order by priority and id itself.
        return ValueTask.FromResult<IReadOnlyList<OutboundRoute>>(Routes.GetValueOrDefault(tenantId) ?? []);
    }
}

/// <summary>A tenant-scoped trunk store: <c>GetAsync</c> and <c>GetByNameAsync</c> see only the tenant's own trunks.</summary>
internal sealed class FakeTrunkStore : TrunkStoreBase
{
    private readonly List<(string Tenant, Trunk Trunk)> _trunks = [];

    public Exception? ThrowOnGet { get; set; }
    public Exception? ThrowOnGetByName { get; set; }

    public FakeTrunkStore Add(string tenantId, Trunk trunk)
    {
        _trunks.Add((tenantId, trunk));
        return this;
    }

    public override ValueTask<Trunk?> GetAsync(long id, string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnGet is not null)
            throw ThrowOnGet;
        return ValueTask.FromResult(_trunks.Where(t => t.Tenant == tenantId && t.Trunk.Id == id).Select(t => t.Trunk).FirstOrDefault());
    }

    public override ValueTask<Trunk?> GetByNameAsync(string name, string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnGetByName is not null)
            throw ThrowOnGetByName;
        return ValueTask.FromResult(_trunks.Where(t => t.Tenant == tenantId && t.Trunk.Name == name).Select(t => t.Trunk).FirstOrDefault());
    }
}

/// <summary>Builds the real resolver over the fakes, so every caller's test runs the production resolution rule.</summary>
internal static class TrunkFakes
{
    public const string RouteHost = "203.0.113.10";

    /// <summary>A stored IP trunk whose <c>MatchHost</c> is a single address.</summary>
    public static Trunk IpTrunk(long id, string name, string host = RouteHost, bool active = true) =>
        new() { Id = id, Name = name, MatchHost = host, IsActive = active };

    /// <summary>A stored trunk with credentials only: no registration URI and no <c>MatchHost</c>.</summary>
    public static Trunk AuthOnlyTrunk(long id, string name) =>
        new() { Id = id, Name = name, AuthUsername = "user", AuthPassword = "secret", IsActive = true };

    public static OutboundRoute Route(long id, string pattern, long trunkId, int priority = 0, long? campaignId = null) =>
        new() { Id = id, Pattern = pattern, TrunkId = trunkId, Priority = priority, CampaignId = campaignId };

    public static DialableTrunkResolver Resolver(OutboundRouteStoreBase? routes, TrunkStoreBase trunks, string? defaultTrunk) =>
        new(routes, trunks, defaultTrunk, NullLogger<DialableTrunkResolver>.Instance);
}
