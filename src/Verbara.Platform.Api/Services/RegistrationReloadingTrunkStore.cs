using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// #385 — decorates the realtime-syncing trunk store and raises <see cref="TrunkRegistrationReloadSignal"/>
/// after a write that adds, changes or removes a registration (a trunk that has, or had, a registration
/// URI). The inner store runs first: when it throws, nothing is signalled and the exception propagates.
/// Reads pass through unchanged.
/// </summary>
internal sealed class RegistrationReloadingTrunkStore : TrunkStoreBase
{
    private readonly TrunkStoreBase _inner;
    private readonly TrunkRegistrationReloadSignal _signal;

    public RegistrationReloadingTrunkStore(TrunkStoreBase inner, TrunkRegistrationReloadSignal signal)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(signal);
        _inner = inner;
        _signal = signal;
    }

    public override ValueTask<Trunk?> GetAsync(long id, string tenantId, CancellationToken ct = default)
        => _inner.GetAsync(id, tenantId, ct);

    public override ValueTask<Trunk?> GetByNameAsync(string name, string tenantId, CancellationToken ct = default)
        => _inner.GetByNameAsync(name, tenantId, ct);

    public override ValueTask<IReadOnlyList<Trunk>> ListAsync(string tenantId, CancellationToken ct = default)
        => _inner.ListAsync(tenantId, ct);

    public override ValueTask<IReadOnlyList<Trunk>> ListActiveAsync(string tenantId, CancellationToken ct = default)
        => _inner.ListActiveAsync(tenantId, ct);

    public override async ValueTask<long> CreateAsync(Trunk trunk, string tenantId, CancellationToken ct = default)
    {
        var id = await _inner.CreateAsync(trunk, tenantId, ct).ConfigureAwait(false);
        if (HasRegistration(trunk))
            _signal.RequestReload();
        return id;
    }

    public override async ValueTask UpdateAsync(Trunk trunk, string tenantId, CancellationToken ct = default)
    {
        // Read the stored row before the write: removing a registration URI must reload too.
        var before = await _inner.GetAsync(trunk.Id, tenantId, ct).ConfigureAwait(false);
        await _inner.UpdateAsync(trunk, tenantId, ct).ConfigureAwait(false);
        if (HasRegistration(trunk) || HasRegistration(before))
            _signal.RequestReload();
    }

    public override async ValueTask DeleteAsync(long id, string tenantId, CancellationToken ct = default)
    {
        var before = await _inner.GetAsync(id, tenantId, ct).ConfigureAwait(false);
        await _inner.DeleteAsync(id, tenantId, ct).ConfigureAwait(false);
        if (HasRegistration(before))
            _signal.RequestReload();
    }

    private static bool HasRegistration(Trunk? trunk) => !string.IsNullOrEmpty(trunk?.RegistrationUri);
}
