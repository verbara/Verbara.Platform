using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// agent-started-conversations (whatsapp-works-for-real task 7.5, design D14) against a real Postgres: a
/// conversation an agent starts in its own name does not exist before the commit, so metering's unchanged
/// ownership writer must INSERT it born <see cref="ConversationState.Active"/> and owned, together with its
/// existing-kind ledger row — and leave nothing when the ledger write fails. <c>FindActiveByContactAsync</c> is
/// the lookup the starter's reuse and <c>conversation-open-elsewhere</c> checks read.
/// </summary>
public sealed class AgentStartedConversationLedgerTests : IClassFixture<AgentIdentityFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId T1 = new("t1");
    private static readonly EntityId Contact = EntityId.From("contact-wa-1");
    private readonly AgentIdentityFixture _fixture;

    public AgentStartedConversationLedgerTests(AgentIdentityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task CommitOwnershipAsync_ShouldInsertBornActiveOwnedConversation_WhenAgentStartsIt()
    {
        var h = await PreparedAsync();
        var store = new PostgresConversationStore(h.Ds);
        (await store.FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None))
            .Should().BeNull("the start persists nothing before the commit");

        await h.Writer.CommitOwnershipAsync(
            BornActive("c-start", "a1"), new OwnershipChange(OwnershipChangeKind.TakenOver, "u-a1"), CancellationToken.None);

        var found = await store.FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None);
        found.Should().NotBeNull();
        found!.ConversationId.Value.Should().Be("c-start");
        found.State.Should().Be(ConversationState.Active);
        found.Owner.Should().Be(ConversationOwner.ForAgent(EntityId.From("a1")));
        found.CreatedBy.Should().Be("u-a1");

        var ownershipRows = (await h.EventsAsync("t1")).Where(e => LicenseAgentEventKinds.IsOwnership(e.Kind)).ToList();
        var row = ownershipRows.Should().ContainSingle().Subject;
        (row.Kind, row.AgentId, row.UserId, row.ConversationId, row.ActorUserId, row.Counted)
            .Should().Be((LicenseAgentEventKinds.ConversationTakenOver, "a1", "u-a1", "c-start", "u-a1", (bool?)true));
    }

    [Fact]
    public async Task CommitOwnershipAsync_ShouldLeaveNoConversation_WhenLedgerInsertFailsForAStart()
    {
        var h = await PreparedAsync();
        await h.FailLedgerInsertsAsync();

        var act = () => h.Writer.CommitOwnershipAsync(
            BornActive("c-start", "a1"), new OwnershipChange(OwnershipChangeKind.TakenOver, "u-a1"), CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>();
        (await new PostgresConversationStore(h.Ds).FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None))
            .Should().BeNull("the conversation and its ledger row commit together or not at all");
        (await h.CountAsync("SELECT COUNT(*) FROM conversations WHERE conversation_id = 'c-start'")).Should().Be(0);
    }

    [Fact]
    public async Task FindActiveByContactAsync_ShouldReturnTheOtherAgentsConversation_WhenStartedByAnotherAgent()
    {
        var h = await PreparedAsync();
        await h.CreateAgentAsync("t1", "a2");
        await h.Writer.CommitOwnershipAsync(
            BornActive("c-first", "a1"), new OwnershipChange(OwnershipChangeKind.TakenOver, "u-a1"), CancellationToken.None);

        var found = await new PostgresConversationStore(h.Ds)
            .FindActiveByContactAsync(T1, Contact, ChannelType.WhatsApp, CancellationToken.None);

        // What a second agent's start reads to refuse with conversation-open-elsewhere (owner kind Agent, not a2).
        found!.ConversationId.Value.Should().Be("c-first");
        found.Owner!.Kind.Should().Be(ConversationOwnerKind.Agent);
        found.Owner.OwnerId.Should().NotBe(EntityId.From("a2"));
    }

    private async Task<LicenseLedgerHarness> PreparedAsync()
    {
        var h = await LicenseLedgerHarness.CreateAsync(_fixture, T0);
        await h.TenantAsync("t1", LicenseLedgerHarness.Customer);
        await h.CreateAgentAsync("t1", "a1");
        return h;
    }

    private static Conversation BornActive(string conversationId, string ownerAgentId) => new()
    {
        ConversationId = EntityId.From(conversationId),
        TenantId = T1,
        ContactId = Contact,
        Channel = ChannelType.WhatsApp,
        State = ConversationState.Active,
        Owner = ConversationOwner.ForAgent(EntityId.From(ownerAgentId)),
        CreatedAt = T0,
        UpdatedAt = T0,
        CreatedBy = "u-" + ownerAgentId,
        UpdatedBy = "u-" + ownerAgentId,
    };
}
