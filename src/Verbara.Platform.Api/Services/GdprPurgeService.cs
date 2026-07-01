using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

internal sealed class GdprPurgeService : IGdprPurgeService
{
    private readonly IContactStore _contactStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IUserStore _userStore;
    private readonly IPurgeLogStore _purgeLogStore;
    private readonly IAuditStore _auditStore;

    public GdprPurgeService(
        IContactStore contactStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IUserStore userStore,
        IPurgeLogStore purgeLogStore,
        IAuditStore auditStore)
    {
        _contactStore = contactStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _userStore = userStore;
        _purgeLogStore = purgeLogStore;
        _auditStore = auditStore;
    }


    public async Task<PurgeResult> PurgeContactDataAsync(
        string tenantId, string contactId, string performedBy,
        string reason, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var cid = EntityId.From(contactId);
        var entitiesDeleted = new Dictionary<string, int>();

        // 1. Find all conversations for this contact (need IDs for message deletion)
        var conversations = await _conversationStore.ListByContactAsync(tid, cid, ct);
        var conversationIds = conversations.Select(c => c.ConversationId).ToList();

        // 2. Delete messages first (referential integrity -- messages reference conversations)
        if (conversationIds.Count > 0)
        {
            var messagesDeleted = await _messageStore.DeleteByConversationIdsAsync(tid, conversationIds, ct);
            if (messagesDeleted > 0)
                entitiesDeleted["messages"] = messagesDeleted;
        }

        // 3. Art. 17 redaction of autonomous-disposition audit records (ADR-0034 Decision 4).
        //    These AI-actor records reference the contact's conversations. We redact the
        //    contact-identifying linkage (conversation ID) while RETAINING the decision-fact
        //    (node path, confidence, timestamp, AI actor) under the Art. 17(3) legal-defence
        //    exemption — they are anonymised in place, NOT deleted. Done before the conversations
        //    are deleted, while we still hold their IDs.
        if (conversationIds.Count > 0)
        {
            var auditRedacted = await _auditStore.RedactContactLinkageAsync(
                tid, conversationIds.Select(c => c.Value).ToList(), ct);
            if (auditRedacted > 0)
                entitiesDeleted["auditRecordsRedacted"] = auditRedacted;
        }

        // 4. Delete conversations
        var conversationsDeleted = await _conversationStore.DeleteByContactAsync(tid, cid, ct);
        if (conversationsDeleted > 0)
            entitiesDeleted["conversations"] = conversationsDeleted;

        // 5. Delete auth events for linked user (if any)
        var contact = await _contactStore.GetByIdAsync(tid, cid, ct);
        if (contact is not null)
        {
            var emailAddress = contact.Addresses.FirstOrDefault(a => a.Channel == ChannelType.Email);
            if (emailAddress is not null)
            {
                var user = await _userStore.GetByEmailAsync(tid, emailAddress.Address, ct);
                if (user is not null)
                {
                    var authEventsDeleted = await _authEventStore.DeleteByUserAsync(tenantId, user.UserId.Value, ct);
                    if (authEventsDeleted > 0)
                        entitiesDeleted["authEvents"] = authEventsDeleted;
                }
            }
        }

        // 6. Delete contact itself
        await _contactStore.DeleteAsync(tid, cid, ct);
        entitiesDeleted["contact"] = 1;

        // 7. Write tombstone (NO PII -- only metadata)
        var purgeId = Guid.NewGuid().ToString("N");
        var purgedAt = DateTimeOffset.UtcNow;
        var purgeEntry = new PurgeEntry
        {
            PurgeId = purgeId,
            TenantId = tenantId,
            SubjectType = "contact",
            SubjectId = contactId,
            PerformedBy = performedBy,
            Reason = reason,
            EntitiesDeleted = entitiesDeleted,
            PurgedAt = purgedAt,
        };
        await _purgeLogStore.SaveAsync(purgeEntry, ct);

        return new PurgeResult(purgeId, entitiesDeleted, purgedAt);
    }

    public async Task<PurgeResult> PurgeUserDataAsync(
        string tenantId, string userId, string performedBy,
        string reason, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var uid = EntityId.From(userId);
        var entitiesDeleted = new Dictionary<string, int>();

        // 1. Delete auth events for the user
        var authEventsDeleted = await _authEventStore.DeleteByUserAsync(tenantId, userId, ct);
        if (authEventsDeleted > 0)
            entitiesDeleted["authEvents"] = authEventsDeleted;

        // 2. Delete the user record itself
        await _userStore.DeleteAsync(tid, uid, ct);
        entitiesDeleted["user"] = 1;

        // 3. Write tombstone (NO PII — only metadata)
        var purgeId = Guid.NewGuid().ToString("N");
        var purgedAt = DateTimeOffset.UtcNow;
        var purgeEntry = new PurgeEntry
        {
            PurgeId = purgeId,
            TenantId = tenantId,
            SubjectType = "user",
            SubjectId = userId,
            PerformedBy = performedBy,
            Reason = reason,
            EntitiesDeleted = entitiesDeleted,
            PurgedAt = purgedAt,
        };
        await _purgeLogStore.SaveAsync(purgeEntry, ct);

        return new PurgeResult(purgeId, entitiesDeleted, purgedAt);
    }

    public async Task<UserPurgePreview> PreviewUserPurgeAsync(
        string tenantId, string userId, CancellationToken ct)
    {
        var authEvents = await _authEventStore.ListAllByUserAsync(tenantId, userId, ct);

        return new UserPurgePreview(
            UserId: userId,
            TenantId: tenantId,
            AuthEventCount: authEvents.Count,
            AuditTrailCount: 0,
            PreviewedAt: DateTimeOffset.UtcNow);
    }
}
