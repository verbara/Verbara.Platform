using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>Dev/test in-memory <see cref="ISupervisorListenProfileStore"/> (supervisor-silent-listen, design D12).</summary>
internal sealed class InMemorySupervisorListenProfileStore : ISupervisorListenProfileStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string User), SupervisorListenProfile> _items = [];

    public Task<SupervisorListenProfile?> GetAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_items.TryGetValue((tenantId.Value, userId.Value), out var p) ? Copy(p) : null);
    }

    public Task<IReadOnlyList<SupervisorListenProfile>> ListWithCredentialsAsync(TenantId tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<SupervisorListenProfile> list = [.. _items.Values
                .Where(p => p.TenantId == tenantId && p.HasCredentials)
                .OrderBy(p => p.UserId.Value, StringComparer.Ordinal)
                .Select(Copy)];
            return Task.FromResult(list);
        }
    }

    public Task<bool> SaveAsync(SupervisorListenProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (profile.SipExtension is { } extension && _items.Values.Any(p =>
                    p.TenantId == profile.TenantId && p.UserId != profile.UserId
                    && string.Equals(p.SipExtension, extension, StringComparison.Ordinal)))
                return Task.FromResult(false);

            _items[(profile.TenantId.Value, profile.UserId.Value)] = Copy(profile);
            return Task.FromResult(true);
        }
    }

    public Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        lock (_gate)
            _items.Remove((tenantId.Value, userId.Value));
        return Task.CompletedTask;
    }

    private static SupervisorListenProfile Copy(SupervisorListenProfile p) => new()
    {
        TenantId = p.TenantId,
        UserId = p.UserId,
        SipExtension = p.SipExtension,
        SipPassword = p.SipPassword,
        PhoneNumber = p.PhoneNumber,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
