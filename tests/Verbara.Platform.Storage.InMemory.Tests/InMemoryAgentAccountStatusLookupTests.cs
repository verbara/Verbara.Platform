using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// licensed-agent-metering slice 1 (tasks.md 1.2): the in-memory account-status seam and the in-memory
/// agent store's one-agent-per-user rule, the parity twins of the Postgres implementation and of
/// <c>ux_agents_tenant_user</c>.
/// </summary>
public sealed class InMemoryAgentAccountStatusLookupTests
{
    private static readonly TenantId Tenant = new("t1");

    private static User MakeUser(string id, UserStatus status, TenantId? tenant = null) => new()
    {
        UserId = EntityId.From(id),
        TenantId = tenant ?? Tenant,
        Email = $"{id}@example.test",
        DisplayName = id,
        Role = UserRole.Agent,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AgentAccountStatusLookup_ShouldReturnOnlyActiveUsersOfTheTenant_WhenAskedInBatch()
    {
        var users = new InMemoryUserStore();
        await users.CreateAsync(MakeUser("active", UserStatus.Active), CancellationToken.None);
        await users.CreateAsync(MakeUser("suspended", UserStatus.Suspended), CancellationToken.None);
        await users.CreateAsync(MakeUser("deactivated", UserStatus.Deactivated), CancellationToken.None);
        await users.CreateAsync(MakeUser("other", UserStatus.Active, new TenantId("t2")), CancellationToken.None);
        var sut = new InMemoryAgentAccountStatusLookup(users);

        var active = await sut.GetActiveUserIdsAsync(
            Tenant,
            [EntityId.From("active"), EntityId.From("suspended"), EntityId.From("deactivated"), EntityId.From("missing"), EntityId.From("other")],
            CancellationToken.None);

        active.Should().BeEquivalentTo([EntityId.From("active")]);
    }

    [Fact]
    public async Task AgentAccountStatusLookup_ShouldSeeAStatusWrite_OnTheNextCall()
    {
        var users = new InMemoryUserStore();
        await users.CreateAsync(MakeUser("u1", UserStatus.Active), CancellationToken.None);
        IAgentAccountStatusLookup sut = new InMemoryAgentAccountStatusLookup(users);
        (await sut.IsActiveAsync(Tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeTrue();

        await users.UpdateAdminFieldsAsync(
            Tenant, EntityId.From("u1"), new AdminFieldsChange { Status = UserStatus.Suspended }, DateTimeOffset.UtcNow, "admin",
            CancellationToken.None);

        (await sut.IsActiveAsync(Tenant, EntityId.From("u1"), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldThrowEntityAlreadyExists_WhenUserAlreadyOwnsAgent()
    {
        var store = new InMemoryAgentStore();
        var first = MakeAgent("u1");
        await store.SaveAsync(first, CancellationToken.None);

        var second = () => store.SaveAsync(MakeAgent("u1"), CancellationToken.None);

        await second.Should().ThrowAsync<EntityAlreadyExistsException>();
        await store.SaveAsync(first, CancellationToken.None); // re-saving the same agent is an update
        await store.SaveAsync(MakeAgent("u1", new TenantId("t2")), CancellationToken.None); // other tenant
        (await store.GetByUserIdAsync(Tenant, EntityId.From("u1"), CancellationToken.None))!.AgentId.Should().Be(first.AgentId);
    }

    [Fact]
    public async Task SaveAsync_ShouldLetExactlyOneSucceed_WhenCreationsForOneUserRace()
    {
        var store = new InMemoryAgentStore();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await store.SaveAsync(MakeAgent("racer"), CancellationToken.None);
                return true;
            }
            catch (EntityAlreadyExistsException)
            {
                return false;
            }
        })));

        outcomes.Count(created => created).Should().Be(1);
    }

    private static Agent MakeAgent(string userId, TenantId? tenant = null) => new()
    {
        AgentId = EntityId.New(),
        TenantId = tenant ?? Tenant,
        UserId = EntityId.From(userId),
        DisplayName = "Agent",
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
