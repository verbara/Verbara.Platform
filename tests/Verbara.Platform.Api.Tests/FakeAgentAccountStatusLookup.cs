using Verbara.Platform.Core;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Test double for <see cref="IAgentAccountStatusLookup"/>: every user is Active unless named in
/// <see cref="Inactive"/>. Records each call so a test can prove the seam was consulted.
/// </summary>
internal sealed class FakeAgentAccountStatusLookup : IAgentAccountStatusLookup
{
    public HashSet<EntityId> Inactive { get; } = [];

    public int Calls { get; private set; }

    public Task<IReadOnlySet<EntityId>> GetActiveUserIdsAsync(
        TenantId tenantId, IReadOnlyCollection<EntityId> userIds, CancellationToken ct)
    {
        Calls++;
        IReadOnlySet<EntityId> active = userIds.Where(id => !Inactive.Contains(id)).ToHashSet();
        return Task.FromResult(active);
    }
}
