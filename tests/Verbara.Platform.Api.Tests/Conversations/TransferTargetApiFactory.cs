using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The conversation-ownership host with what the transfer-target pickers need: an account-status lookup that records
/// each call and still answers from the real users, and helpers that seed queues and agents with the names, ids and
/// states a test asserts on. A picker counts the whole tenant, so a test starts from <see cref="ClearTargets"/>.
/// </summary>
public sealed class TransferTargetApiFactory : ConversationOwnershipApiFactory
{
    private readonly ConcurrentQueue<IReadOnlyCollection<EntityId>> _lookups = new();

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        var real = services.Last(descriptor => descriptor.ServiceType == typeof(IAgentAccountStatusLookup));
        services.Remove(real);
        services.AddSingleton<IAgentAccountStatusLookup>(provider =>
            new RecordingLookup((IAgentAccountStatusLookup)real.ImplementationFactory!(provider), _lookups));
    }

    /// <summary>Removes every queue and agent of the customer tenant.</summary>
    public void ClearTargets()
    {
        using var scope = Services.CreateScope();
        var queues = scope.ServiceProvider.GetRequiredService<IQueueStore>();
        foreach (var queue in queues.ListAllAsync(Tenant, CancellationToken.None).GetAwaiter().GetResult())
            queues.DeleteAsync(Tenant, queue.QueueId, CancellationToken.None).GetAwaiter().GetResult();

        var agents = scope.ServiceProvider.GetRequiredService<IAgentStore>();
        foreach (var agent in agents.ListAllAsync(Tenant, CancellationToken.None).GetAwaiter().GetResult())
            agents.DeleteAsync(Tenant, agent.AgentId, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>A queue of the customer tenant with exactly this name, and this id when one is given.</summary>
    public Queue SeedQueue(string name, bool isActive = true, string? id = null)
    {
        var queue = new Queue
        {
            QueueId = EntityId.From(id ?? $"queue-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            Name = name,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueueStore>().SaveAsync(queue, CancellationToken.None)
            .GetAwaiter().GetResult();
        return queue;
    }

    /// <summary>
    /// An agent of the customer tenant with exactly this display name and state, held by a new user with
    /// <paramref name="status"/>, or by no user at all when <paramref name="hasUser"/> is false.
    /// </summary>
    public Agent SeedAgent(
        string displayName,
        AgentState state = AgentState.Available,
        UserStatus status = UserStatus.Active,
        string? id = null,
        bool hasUser = true)
    {
        var userId = $"target-user-{Guid.NewGuid():N}";
        if (hasUser)
            SaveUser(userId, CustomerTenantId, UserRole.Agent, status);

        var agent = new Agent
        {
            AgentId = EntityId.From(id ?? $"agent-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            UserId = EntityId.From(userId),
            DisplayName = displayName,
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Save(agent);
        return agent;
    }

    /// <summary>Stores <paramref name="agent"/> as it is now.</summary>
    public void Save(Agent agent)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>The id sets of every account-status lookup that asked about <paramref name="userId"/>.</summary>
    public IReadOnlyList<IReadOnlyCollection<EntityId>> LookupsOf(EntityId userId) =>
        _lookups.Where(ids => ids.Contains(userId)).ToList();

    private sealed class RecordingLookup(
        IAgentAccountStatusLookup inner,
        ConcurrentQueue<IReadOnlyCollection<EntityId>> lookups) : IAgentAccountStatusLookup
    {
        public Task<IReadOnlySet<EntityId>> GetActiveUserIdsAsync(
            TenantId tenantId, IReadOnlyCollection<EntityId> userIds, CancellationToken ct)
        {
            lookups.Enqueue(userIds);
            return inner.GetActiveUserIdsAsync(tenantId, userIds, ct);
        }
    }
}
