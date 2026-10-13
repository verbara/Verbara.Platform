using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;

namespace Verbara.Platform.Api.Services.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D1): a trunk Platform can dial. <see cref="EndpointId"/> is the Asterisk
/// endpoint the trunk sync provisions (<c>t-{id}</c>) and <see cref="Host"/> the address its INVITE goes to.
/// </summary>
internal sealed record DialableTrunkTarget(long TrunkId, string EndpointId, string Host)
{
    /// <summary>The only spelling of the dial form in code: <c>PJSIP/t-{id}/sip:{number}@{host}</c>.</summary>
    public string Channel(string number) => $"PJSIP/{EndpointId}/sip:{number}@{Host}";
}

/// <summary>What a resolution found: a dialable trunk, no trunk, or a trunk that names no host.</summary>
internal enum DialableTrunkResolutionKind
{
    Dialable,
    NoTrunk,
    NotDialable,
}

/// <summary>
/// The answer of <see cref="DialableTrunkResolver.ResolveAsync"/>. Callers switch on <see cref="Kind"/>; nothing throws.
/// <see cref="TrunkId"/> is set for <see cref="DialableTrunkResolutionKind.Dialable"/> and
/// <see cref="DialableTrunkResolutionKind.NotDialable"/>.
/// </summary>
internal sealed record DialableTrunkResolution(DialableTrunkResolutionKind Kind, DialableTrunkTarget? Target, long? TrunkId)
{
    public static readonly DialableTrunkResolution NoTrunk = new(DialableTrunkResolutionKind.NoTrunk, null, null);

    public static DialableTrunkResolution Dialable(DialableTrunkTarget target) =>
        new(DialableTrunkResolutionKind.Dialable, target, target.TrunkId);

    public static DialableTrunkResolution NotDialable(long trunkId) =>
        new(DialableTrunkResolutionKind.NotDialable, null, trunkId);
}

/// <summary>
/// outbound-calls-reach-the-trunk (design D1, D2): the one trunk resolution behind every Platform caller that dials the
/// public network (click-to-dial, the external transfer, the callback rescue and the listen by phone).
/// <para>
/// The tenant's own outbound routes come first: of those that are not campaign-scoped and whose pattern is a prefix
/// of the number, in ascending priority then id, the first whose trunk is stored for the tenant and active. The
/// route's trunk is loaded from the tenant's stored trunks by its id. Otherwise the configured default trunk is
/// looked up by name among the tenant's stored trunks. The resolved trunk is dialed through its endpoint id
/// <c>t-{id}</c> and the host of <see cref="DialHost.From"/>; a trunk with no host is not dialable.
/// </para>
/// </summary>
internal sealed partial class DialableTrunkResolver
{
    private readonly OutboundRouteStoreBase? _routes;
    private readonly TrunkStoreBase _trunks;
    private readonly string? _defaultTrunk;
    private readonly ILogger<DialableTrunkResolver> _logger;

    public DialableTrunkResolver(
        OutboundRouteStoreBase? routes, TrunkStoreBase trunks, string? defaultTrunk, ILogger<DialableTrunkResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(trunks);
        ArgumentNullException.ThrowIfNull(logger);
        _routes = routes;
        _trunks = trunks;
        _defaultTrunk = defaultTrunk;
        _logger = logger;
    }

    /// <summary>The trunk <paramref name="number"/> is dialed over in <paramref name="tenantId"/>.</summary>
    public async ValueTask<DialableTrunkResolution> ResolveAsync(TenantId tenantId, string number, CancellationToken ct)
    {
        var trunk = await RouteTrunkAsync(tenantId, number, ct).ConfigureAwait(false)
                    ?? await DefaultTrunkAsync(tenantId, ct).ConfigureAwait(false);
        if (trunk is null)
            return DialableTrunkResolution.NoTrunk;

        var host = DialHost.From(trunk);
        if (host is null)
        {
            LogNotDialable(trunk.Id);
            return DialableTrunkResolution.NotDialable(trunk.Id);
        }

        return DialableTrunkResolution.Dialable(new DialableTrunkTarget(trunk.Id, $"t-{trunk.Id}", host));
    }

    /// <summary>The first matching route's trunk, reloaded from the tenant's stored trunks; null falls through.</summary>
    private async ValueTask<Trunk?> RouteTrunkAsync(TenantId tenantId, string number, CancellationToken ct)
    {
        if (_routes is null)
            return null;

        try
        {
            var routes = await _routes.ListByPriorityAsync(tenantId.Value, ct).ConfigureAwait(false);
            var matches = routes
                .Where(r => r.CampaignId is null
                            && !string.IsNullOrEmpty(r.Pattern)
                            && number.StartsWith(r.Pattern, StringComparison.Ordinal))
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.Id);
            foreach (var route in matches)
            {
                var trunk = await _trunks.GetAsync(route.TrunkId, tenantId.Value, ct).ConfigureAwait(false);
                if (trunk is { IsActive: true })
                    return trunk;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRouteFailed(ex.Message);
        }

        return null;
    }

    /// <summary>The configured default trunk, looked up by name among the tenant's stored trunks.</summary>
    private async ValueTask<Trunk?> DefaultTrunkAsync(TenantId tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_defaultTrunk))
            return null;

        Trunk? trunk;
        try
        {
            trunk = await _trunks.GetByNameAsync(_defaultTrunk, tenantId.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDefaultLookupFailed(_defaultTrunk, ex.Message);
            return null;
        }

        if (trunk is { IsActive: true })
            return trunk;

        LogDefaultNotFound(_defaultTrunk);
        return null;
    }

    [LoggerMessage(EventId = 9160, Level = LogLevel.Warning,
        Message = "[OUTBOUND-TRUNK] Outbound route lookup failed (falling back to the default trunk): {Reason}")]
    private partial void LogRouteFailed(string reason);

    [LoggerMessage(EventId = 9161, Level = LogLevel.Warning,
        Message = "[OUTBOUND-TRUNK] Default trunk '{DefaultTrunk}' could not be looked up: {Reason}")]
    private partial void LogDefaultLookupFailed(string defaultTrunk, string reason);

    [LoggerMessage(EventId = 9162, Level = LogLevel.Warning,
        Message = "[OUTBOUND-TRUNK] Default trunk '{DefaultTrunk}' names no active stored trunk of the tenant (Asterisk:Outbound:DefaultTrunk must be a trunk name)")]
    private partial void LogDefaultNotFound(string defaultTrunk);

    [LoggerMessage(EventId = 9163, Level = LogLevel.Warning,
        Message = "[OUTBOUND-TRUNK] Trunk {TrunkId} has no dialable host (no registration URI and no single-address MatchHost)")]
    private partial void LogNotDialable(long trunkId);
}
