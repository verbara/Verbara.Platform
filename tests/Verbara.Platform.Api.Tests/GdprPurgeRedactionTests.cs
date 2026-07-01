using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0034 Decision 4 (GDPR Art. 17): the contact purge MUST redact the contact-identifying linkage
/// on autonomous-disposition audit records while RETAINING the decision-fact — never delete them.
/// Wires the InMemory stores directly into <see cref="GdprPurgeService"/> so the redaction wiring is
/// exercised end-to-end without the full API host.
/// </summary>
public sealed class GdprPurgeRedactionTests
{
    private static readonly TenantId Tenant = new("tenant-purge");

    [Fact]
    public async Task PurgeContactDataAsync_ShouldRedactAutonomousAuditLinkage_AndRetainDecisionFact()
    {
        var contactStore = new InMemoryContactStore();
        var conversationStore = new InMemoryConversationStore();
        var messageStore = new InMemoryMessageStore();
        var authEventStore = new InMemoryAuthEventStore();
        var userStore = new InMemoryUserStore();
        var purgeLogStore = new InMemoryPurgeLogStore();
        var auditStore = new InMemoryAuditStore();

        var contactId = EntityId.New();
        await contactStore.SaveAsync(new Contact
        {
            ContactId = contactId,
            TenantId = Tenant,
            FirstName = "Erase",
            LastName = "Me",
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var conversationId = EntityId.New();
        await conversationStore.SaveAsync(new Conversation
        {
            ConversationId = conversationId,
            TenantId = Tenant,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Closed,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // An AI-actor autonomous-disposition audit record referencing the contact's conversation.
        var auditService = new DefaultAuditService(auditStore, new SystemClock());
        await auditService.RecordAsync(
            Tenant,
            category: "conversations",
            action: AutonomousAuditRedaction.AutonomousCommitAction,
            severity: "info",
            actorId: "verbara:ai:autonomous-worker",
            actorType: "ai",
            targetId: conversationId.Value,
            targetType: "Conversation",
            metadata: new Dictionary<string, string>
            {
                ["node_path"] = "Sales > Upgrade > Completed",
                ["leaf_node_id"] = "leaf-1",
                ["confidence"] = "0.9800",
                ["tenant"] = Tenant.Value,
                ["conversation"] = conversationId.Value,
            },
            retainUntil: DateTimeOffset.UtcNow.AddDays(30),
            ct: CancellationToken.None);

        var service = new GdprPurgeService(
            contactStore, conversationStore, messageStore,
            authEventStore, userStore, purgeLogStore, auditStore);

        var result = await service.PurgeContactDataAsync(
            Tenant.Value, contactId.Value, "admin", "Subject erasure request", CancellationToken.None);

        // The purge reports the redaction, and the record is retained (not deleted) with linkage gone.
        result.EntitiesDeleted.Should().ContainKey("auditRecordsRedacted");
        result.EntitiesDeleted["auditRecordsRedacted"].Should().Be(1);

        var audit = await auditStore.SearchAsync(
            Tenant, new AuditQuery(Action: AutonomousAuditRedaction.AutonomousCommitAction), CancellationToken.None);
        audit.Items.Should().ContainSingle("the decision fact is retained, not deleted");
        var entry = audit.Items[0];
        entry.TargetId.Should().BeNull("the contact-identifying conversation linkage is redacted");
        entry.Metadata!["conversation"].Should().Be("[redacted]");
        entry.Metadata!["node_path"].Should().Be("Sales > Upgrade > Completed", "the decision fact survives");
        entry.Metadata!["confidence"].Should().Be("0.9800");
        entry.ActorType.Should().Be("ai");
    }
}
