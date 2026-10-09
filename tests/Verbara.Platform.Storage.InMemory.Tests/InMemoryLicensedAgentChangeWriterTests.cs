using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.1/5.2, InMemory parity): the in-memory writer has the Postgres
/// writer's outcomes — a ledger failure leaves the domain row unchanged (one test per method), a first change
/// anchors and baselines the chain, and a licence change re-anchors it.
/// </summary>
public sealed class InMemoryLicensedAgentChangeWriterTests : IDisposable
{
    private static readonly TenantId T1 = new("t1");
    private readonly InMemoryAgentStore _agents = new();
    private readonly InMemoryUserStore _users = new();
    private readonly InMemoryConversationStore _conversations = new();
    private readonly FixedLicenseIdSource _licenses = new("lic-1");
    private readonly InMemoryLicenseAgentLedger _ledger;
    private readonly InMemoryLicensedAgentChangeWriter _writer;

    public InMemoryLicensedAgentChangeWriterTests()
    {
        _ledger = new InMemoryLicenseAgentLedger(_licenses, new FixedClock());
        _writer = new InMemoryLicensedAgentChangeWriter(_agents, _users, _conversations, _ledger);
    }

    public void Dispose() => _writer.Dispose();

    [Fact]
    public async Task CommitAgentCreatedAsync_ShouldLeaveNoAgent_WhenTheLedgerFails()
    {
        await UserAsync("u1");
        _ledger.FailNextAppend = true;

        var act = () => _writer.CommitAgentCreatedAsync(NewAgent("a1", "u1"), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _agents.GetByIdAsync(T1, EntityId.From("a1"), CancellationToken.None)).Should().BeNull();
        _ledger.Events("t1").Should().BeEmpty();
    }

    [Fact]
    public async Task CommitAgentDeletedAsync_ShouldKeepTheAgent_WhenTheLedgerFails()
    {
        await AgentAsync("a1", "u1");
        _ledger.FailNextAppend = true;

        var act = () => _writer.CommitAgentDeletedAsync(T1, EntityId.From("a1"), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _agents.GetByIdAsync(T1, EntityId.From("a1"), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task CommitOwnershipAsync_ShouldKeepTheOldOwner_WhenTheLedgerFails()
    {
        await AgentAsync("a1", "u1");
        await AgentAsync("a2", "u2");
        var conversation = Conversation("a1");
        await _conversations.SaveAsync(conversation, CancellationToken.None);
        _ledger.FailNextAppend = true;

        var moved = Conversation("a2");
        var act = () => _writer.CommitOwnershipAsync(moved, new OwnershipChange(OwnershipChangeKind.TakenOver, "u2"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _conversations.GetByIdAsync(T1, conversation.ConversationId, CancellationToken.None))!.Owner!.OwnerId
            .Should().Be(EntityId.From("a1"));
    }

    [Fact]
    public async Task CommitUserStatusChangedAsync_ShouldKeepTheStatus_WhenTheLedgerFails()
    {
        await AgentAsync("a1", "u1");
        _ledger.FailNextAppend = true;

        var act = () => _writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u1"), new AdminFieldsChange { Status = UserStatus.Suspended }, DateTimeOffset.UtcNow, "admin", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _users.GetByIdAsync(T1, EntityId.From("u1"), CancellationToken.None))!.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task CommitUserDeletedAsync_ShouldKeepTheUserAndItsAgent_WhenTheLedgerFails()
    {
        await AgentAsync("a1", "u1");
        _ledger.FailNextAppend = true;

        var act = () => _writer.CommitUserDeletedAsync(T1, EntityId.From("u1"), "dpo", deleteOwnedAgent: true, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _users.GetByIdAsync(T1, EntityId.From("u1"), CancellationToken.None)).Should().NotBeNull();
        (await _agents.GetByIdAsync(T1, EntityId.From("a1"), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task FirstChange_ShouldAnchorWithABaselineOfTheAgentsBeforeIt_AndALicenceChangeReanchors()
    {
        await UserAsync("u-old");
        await _agents.SaveAsync(NewAgent("a-old", "u-old"), CancellationToken.None); // existed before the upgrade

        await AgentAsync("a1", "u1");
        _licenses.CurrentLicenseId = "lic-2";
        await _writer.CommitAgentDeletedAsync(T1, EntityId.From("a1"), "admin", CancellationToken.None);

        var rows = _ledger.Events("t1");
        rows.Select(r => (r.Kind, r.AgentId, r.LicenseId)).Should().Equal(
            (LicenseAgentEventKinds.ChainAnchored, null, "lic-1"),
            (LicenseAgentEventKinds.AgentBaseline, "a-old", "lic-1"),
            (LicenseAgentEventKinds.AgentCreated, "a1", "lic-1"),
            (LicenseAgentEventKinds.ChainReanchored, null, "lic-2"),
            (LicenseAgentEventKinds.AgentDeleted, "a1", "lic-2"));
        LicenseAgentChain.Verify("t1", "lic-1", rows, []).Should().BeNull();
    }

    [Fact]
    public async Task CommitUserDeletedAsync_ShouldRefuseAndWriteNothing_WhenTheUserOwnsAnAgentAndTheCallerKeepsIt()
    {
        await AgentAsync("a1", "u1");
        var before = _ledger.Events("t1").Count;

        var deletion = await _writer.CommitUserDeletedAsync(T1, EntityId.From("u1"), "admin", deleteOwnedAgent: false, CancellationToken.None);

        deletion.Should().Be(LicensedUserDeletion.RefusedOwnsAgent);
        _ledger.Events("t1").Should().HaveCount(before);
        (await _users.GetByIdAsync(T1, EntityId.From("u1"), CancellationToken.None)).Should().NotBeNull();
    }

    private async Task UserAsync(string userId, UserStatus status = UserStatus.Active) =>
        await _users.CreateAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = T1,
            Email = $"{userId}@t1.test",
            DisplayName = userId,
            Role = UserRole.Agent,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

    private async Task AgentAsync(string agentId, string userId)
    {
        await UserAsync(userId);
        await _writer.CommitAgentCreatedAsync(NewAgent(agentId, userId), "admin", CancellationToken.None);
    }

    private static Agent NewAgent(string agentId, string userId) => new()
    {
        AgentId = EntityId.From(agentId),
        TenantId = T1,
        UserId = EntityId.From(userId),
        DisplayName = agentId,
        State = AgentState.Offline,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Conversation Conversation(string ownerAgentId) => new()
    {
        ConversationId = EntityId.From("c1"),
        TenantId = T1,
        ContactId = EntityId.From("contact-1"),
        Channel = ChannelType.WebChat,
        State = ConversationState.Active,
        Owner = ConversationOwner.ForAgent(EntityId.From(ownerAgentId)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    }
}
