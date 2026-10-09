using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// licensed-agent-metering slice 2 (tasks.md 5.2-5.4; licensed-agent-ledger, design D5) against a real
/// Postgres: every write path commits its domain row and its ledger row together — a ledger failure rolls the
/// domain write back (one test per method) — and each row carries the kind, actor and <c>counted</c> it must.
/// </summary>
public sealed class PostgresLicensedAgentChangeWriterTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId T1 = new("t1");
    private readonly AgentIdentityFixture _fixture;

    public PostgresLicensedAgentChangeWriterTests(AgentIdentityFixture fixture) => _fixture = fixture;

    // ─── 5.2 rollback: a ledger failure leaves the domain row unchanged ─────────

    [Fact]
    public async Task CommitAgentCreatedAsync_ShouldLeaveNoAgent_WhenTheLedgerInsertFails()
    {
        var h = await PreparedAsync();
        await h.UserAsync("t1", "u-new");
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitAgentCreatedAsync(LicenseLedgerHarness.NewAgent("t1", "a-new", "u-new"), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await h.CountAsync("SELECT COUNT(*) FROM agents WHERE agent_id = 'a-new'")).Should().Be(0);
    }

    [Fact]
    public async Task CommitAgentDeletedAsync_ShouldKeepTheAgent_WhenTheLedgerInsertFails()
    {
        var h = await PreparedAsync();
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitAgentDeletedAsync(T1, EntityId.From("a1"), "admin", CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await h.CountAsync("SELECT COUNT(*) FROM agents WHERE agent_id = 'a1'")).Should().Be(1);
    }

    [Fact]
    public async Task CommitOwnershipAsync_ShouldKeepTheOldOwner_WhenTheLedgerInsertFails()
    {
        var h = await PreparedAsync();
        await h.CreateAgentAsync("t1", "a2");
        await h.ConversationAsync("t1", "c1", "a1");
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitOwnershipAsync(
            Conversation("c1", "a2"), new OwnershipChange(OwnershipChangeKind.TakenOver, "u-a2"), CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await h.ScalarTextAsync("SELECT owner_id FROM conversations WHERE conversation_id = 'c1'")).Should().Be("a1");
    }

    [Fact]
    public async Task CommitUserStatusChangedAsync_ShouldKeepTheStatus_WhenTheLedgerInsertFails()
    {
        var h = await PreparedAsync();
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Suspended }, T0, "admin", CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await h.CountAsync("SELECT status FROM users WHERE user_id = 'u-a1'")).Should().Be((int)UserStatus.Active);
    }

    [Fact]
    public async Task CommitUserDeletedAsync_ShouldKeepTheUserAndItsAgent_WhenTheLedgerInsertFails()
    {
        var h = await PreparedAsync();
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitUserDeletedAsync(T1, EntityId.From("u-a1"), "dpo", deleteOwnedAgent: true, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await h.CountAsync("SELECT COUNT(*) FROM users WHERE user_id = 'u-a1'")).Should().Be(1);
        (await h.CountAsync("SELECT COUNT(*) FROM agents WHERE agent_id = 'a1'")).Should().Be(1);
    }

    // ─── what each row records ──────────────────────────────────────────────────

    [Fact]
    public async Task CommitAgentCreatedAsync_ShouldRecordTheActorAndCountability()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);

        await h.CreateAgentAsync("t1", "a-active", userStatus: 0, actor: "admin-1");
        await h.CreateAgentAsync("t1", "a-suspended", userStatus: 1, actor: "admin-1");

        var created = (await h.EventsAsync("t1")).Where(r => r.Kind == LicenseAgentEventKinds.AgentCreated).ToList();
        created.Select(r => (r.AgentId, r.UserId, r.ActorUserId, r.Counted)).Should().Equal(
            ("a-active", "u-a-active", "admin-1", (bool?)true),
            ("a-suspended", "u-a-suspended", "admin-1", false));
    }

    [Fact]
    public async Task CommitUserStatusChangedAsync_ShouldRecordTheNewStatus_WhenTheUserOwnsAnAgent()
    {
        var h = await PreparedAsync();

        var result = await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Suspended }, T0, "admin-2", CancellationToken.None);
        await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Active }, T0, "admin-2", CancellationToken.None);

        result.Outcome.Should().Be(AdminFieldsWriteOutcome.Written);
        var rows = (await h.EventsAsync("t1")).Where(r => r.Kind == LicenseAgentEventKinds.UserStatusChanged).ToList();
        rows.Select(r => (r.AgentId, r.UserStatus, r.Counted, r.ActorUserId)).Should().Equal(
            ("a1", "Suspended", (bool?)false, "admin-2"),
            ("a1", "Active", true, "admin-2"));
    }

    [Fact]
    public async Task CommitUserStatusChangedAsync_ShouldWriteNoRow_WhenTheUserOwnsNoAgentOrTheStatusIsUnchanged()
    {
        var h = await PreparedAsync();
        await h.UserAsync("t1", "u-no-agent");
        var before = (await h.EventsAsync("t1")).Count;

        await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-no-agent"), new AdminFieldsChange { Status = UserStatus.Suspended }, T0, "admin", CancellationToken.None);
        await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { DisplayName = "Renamed" }, T0, "admin", CancellationToken.None);
        await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Active }, T0, "admin", CancellationToken.None);

        (await h.EventsAsync("t1")).Should().HaveCount(before);
        (await h.CountAsync("SELECT status FROM users WHERE user_id = 'u-no-agent'")).Should().Be((int)UserStatus.Suspended);
    }

    [Fact]
    public async Task CommitUserDeletedAsync_ShouldDeleteAgentThenUser_WithBothRows_WhenAskedToDeleteTheOwnedAgent()
    {
        var h = await PreparedAsync();
        await h.Writer.CommitUserStatusChangedAsync(
            T1, EntityId.From("u-a1"), new AdminFieldsChange { Status = UserStatus.Suspended }, T0, "admin", CancellationToken.None);

        var deletion = await h.Writer.CommitUserDeletedAsync(T1, EntityId.From("u-a1"), "dpo", deleteOwnedAgent: true, CancellationToken.None);

        deletion.Should().Be(new LicensedUserDeletion(true, false, "a1"));
        var rows = (await h.EventsAsync("t1")).TakeLast(2).ToList();
        rows.Select(r => (r.Kind, r.AgentId, r.UserId, r.UserStatus, r.Counted, r.ActorUserId)).Should().Equal(
            (LicenseAgentEventKinds.AgentDeleted, "a1", "u-a1", null, (bool?)false, "dpo"),
            (LicenseAgentEventKinds.UserDeleted, "a1", "u-a1", "Suspended", false, "dpo"));
        (await h.CountAsync("SELECT COUNT(*) FROM agents WHERE agent_id = 'a1'")).Should().Be(0);
        (await h.CountAsync("SELECT COUNT(*) FROM users WHERE user_id = 'u-a1'")).Should().Be(0);
    }

    [Fact]
    public async Task CommitUserDeletedAsync_ShouldRefuseAndWriteNothing_WhenTheUserOwnsAnAgentAndTheCallerKeepsIt()
    {
        var h = await PreparedAsync();
        await h.UserAsync("t1", "u-plain");
        var before = (await h.EventsAsync("t1")).Count;

        var refused = await h.Writer.CommitUserDeletedAsync(T1, EntityId.From("u-a1"), "admin", deleteOwnedAgent: false, CancellationToken.None);
        var plain = await h.Writer.CommitUserDeletedAsync(T1, EntityId.From("u-plain"), "admin", deleteOwnedAgent: false, CancellationToken.None);

        refused.Should().Be(LicensedUserDeletion.RefusedOwnsAgent);
        plain.Should().Be(new LicensedUserDeletion(true, false, null));
        (await h.CountAsync("SELECT COUNT(*) FROM users WHERE user_id = 'u-a1'")).Should().Be(1);
        (await h.EventsAsync("t1")).Should().HaveCount(before, "neither a refusal nor a user without an agent writes a row");
    }

    [Theory]
    [InlineData(OwnershipChangeKind.TakenOver, LicenseAgentEventKinds.ConversationTakenOver)]
    [InlineData(OwnershipChangeKind.Transferred, LicenseAgentEventKinds.ConversationTransferred)]
    [InlineData(OwnershipChangeKind.Reassigned, LicenseAgentEventKinds.ConversationReassigned)]
    public async Task CommitOwnershipAsync_ShouldCommitTheOwnerWithItsRow_ForEachCallSite(OwnershipChangeKind kind, string ledgerKind)
    {
        var h = await PreparedAsync();
        await h.CreateAgentAsync("t1", "a2");
        await h.ConversationAsync("t1", "c1", "a1");

        await h.Writer.CommitOwnershipAsync(Conversation("c1", "a2"), new OwnershipChange(kind, "supervisor-1"), CancellationToken.None);

        (await h.ScalarTextAsync("SELECT owner_id FROM conversations WHERE conversation_id = 'c1'")).Should().Be("a2");
        var row = (await h.EventsAsync("t1"))[^1];
        (row.Kind, row.AgentId, row.UserId, row.ConversationId, row.ActorUserId, row.Counted)
            .Should().Be((ledgerKind, "a2", "u-a2", "c1", "supervisor-1", (bool?)true));
    }

    private async Task<LicenseLedgerHarness> PreparedAsync()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.TenantAsync("t1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("t1", "a1");
        return h;
    }

    private static Conversation Conversation(string conversationId, string ownerAgentId) => new()
    {
        ConversationId = EntityId.From(conversationId),
        TenantId = T1,
        ContactId = EntityId.From("contact-1"),
        Channel = ChannelType.Voice,
        State = ConversationState.Active,
        Owner = ConversationOwner.ForAgent(EntityId.From(ownerAgentId)),
        CreatedAt = T0,
    };
}
