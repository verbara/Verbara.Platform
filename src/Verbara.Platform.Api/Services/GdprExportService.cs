using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

internal sealed class GdprExportService : IGdprExportService
{
    private readonly IContactStore _contactStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IAuditStore _auditStore;
    private readonly IUserStore _userStore;

    public GdprExportService(
        IContactStore contactStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IAuditStore auditStore,
        IUserStore userStore)
    {
        _contactStore = contactStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _auditStore = auditStore;
        _userStore = userStore;
    }

    public async Task<GdprExportResult> ExportContactDataAsync(
        string tenantId, string contactId, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var cid = EntityId.From(contactId);

        // 1. Contact profile
        var contact = await _contactStore.GetByIdAsync(tid, cid, ct);

        // 2. All conversations
        var conversations = await _conversationStore.ListByContactAsync(tid, cid, ct);

        // 3. All messages across conversations
        IReadOnlyList<Message> messages = [];
        if (conversations.Count > 0)
        {
            var conversationIds = conversations.Select(c => c.ConversationId).ToList();
            messages = await _messageStore.GetByConversationIdsAsync(tid, conversationIds, ct);
        }

        // 4. Auth events (if linked user exists -- match by contact email)
        IReadOnlyList<AuthEvent>? authEvents = null;
        if (contact is not null)
        {
            // Try to find a user linked to this contact via email address
            var emailAddress = contact.Addresses.FirstOrDefault(a => a.Channel == ChannelType.Email);
            if (emailAddress is not null)
            {
                var user = await _userStore.GetByEmailAsync(tid, emailAddress.Address, ct);
                if (user is not null)
                    authEvents = await _authEventStore.ListAllByUserAsync(tenantId, user.UserId.Value, ct);
            }
        }

        // 5. Audit trail for the contact entity
        var auditEntries = await _auditStore.GetByEntityAsync(tid, "Contact", contactId, ct);

        return new GdprExportResult
        {
            ExportId = Guid.NewGuid().ToString("N"),
            ExportedAt = DateTimeOffset.UtcNow,
            Subject = new GdprSubjectInfo(contactId, tenantId),
            Contact = contact,
            Conversations = conversations.Cast<object>().ToList(),
            Messages = messages.Cast<object>().ToList(),
            AuthEvents = authEvents?.Cast<object>().ToList(),
            AuditEntries = auditEntries.Cast<object>().ToList(),
        };
    }
}
