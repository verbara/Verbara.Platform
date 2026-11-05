using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The audit record of a contact's do-not-contact or consent change, and of a contact delete
/// (contacts-writes-follow-their-permissions, design D17; fixtures/contact-opt-out.v1.json <c>audit</c>). Built as
/// <see cref="DefaultAuditService.RecordAsync"/> builds an entry, v2 integrity hash included, so
/// <see cref="IContactAuditWriter"/> can write it in the same unit as the contact.
/// </summary>
internal static class ContactAuditRecord
{
    /// <summary>The category of every contact record.</summary>
    public const string Category = "contact";

    /// <summary>A <c>PUT /contacts/{id}</c> changed <c>doNotContact</c> or a <c>channelConsent</c> value.</summary>
    public const string ConsentChangedAction = "contact.consent_changed";

    /// <summary>An opt-out changed <c>doNotContact</c> or recorded a channel's no.</summary>
    public const string OptedOutAction = "contact.opted_out";

    /// <summary>A contact was deleted.</summary>
    public const string DeletedAction = "contact.deleted";

    /// <summary>The target type of every contact record.</summary>
    public const string TargetType = "contact";

    /// <summary>The severity of every contact record.</summary>
    public const string Severity = "info";

    /// <summary>How long a contact record is kept: its retention floor is the write time plus this many years.</summary>
    public const int RetentionYears = 5;

    /// <summary>The <c>metadata</c> key of a delete record holding the deleted contact's do-not-contact flag.</summary>
    public const string DoNotContactMetadataKey = "doNotContact";

    /// <summary>The two members a record holds, as the contact has them now, channels by name in channel order.</summary>
    public static ContactConsentAuditState StateOf(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return new ContactConsentAuditState(
            contact.DoNotContact,
            contact.ChannelConsent.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value, StringComparer.Ordinal));
    }

    /// <summary>Whether <paramref name="before"/> and <paramref name="after"/> hold the same values.</summary>
    public static bool Same(ContactConsentAuditState before, ContactConsentAuditState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.DoNotContact == after.DoNotContact
            && before.ChannelConsent.Count == after.ChannelConsent.Count
            && before.ChannelConsent.All(kv => after.ChannelConsent.TryGetValue(kv.Key, out var value) && value == kv.Value);
    }

    /// <summary>
    /// The record of a change of the two members, <paramref name="action"/> being <see cref="ConsentChangedAction"/> or
    /// <see cref="OptedOutAction"/>. The before and after are JSON written through <see cref="ApiJsonContext"/>.
    /// </summary>
    public static AuditEntry Change(
        TenantId tenantId,
        string action,
        ContactActor actor,
        EntityId contactId,
        ContactConsentAuditState before,
        ContactConsentAuditState after,
        DateTimeOffset now) =>
        Build(tenantId, action, actor, contactId, now,
            new AuditChanges(
                JsonSerializer.Serialize(before, ApiJsonContext.Default.ContactConsentAuditState),
                JsonSerializer.Serialize(after, ApiJsonContext.Default.ContactConsentAuditState)),
            metadata: null);

    /// <summary>The record of a delete: the contact's do-not-contact flag in <c>metadata</c>, no name and no address.</summary>
    public static AuditEntry Deleted(TenantId tenantId, ContactActor actor, Contact contact, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DoNotContactMetadataKey] = contact.DoNotContact ? "true" : "false",
        };
        return Build(tenantId, DeletedAction, actor, contact.ContactId, now, changes: null, metadata);
    }

    private static AuditEntry Build(
        TenantId tenantId,
        string action,
        ContactActor actor,
        EntityId contactId,
        DateTimeOffset now,
        AuditChanges? changes,
        IReadOnlyDictionary<string, string>? metadata)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var retainUntil = now.AddYears(RetentionYears);
        return new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = tenantId,
            Action = action,
            Category = Category,
            Severity = Severity,
            ActorId = actor.ActorId,
            ActorType = actor.ActorType,
            TargetId = contactId.Value,
            TargetType = TargetType,
            Changes = changes,
            Metadata = metadata,
            OccurredAt = now,
            RetainUntil = retainUntil,
            IntegrityHash = DefaultAuditService.ComputeIntegrityHashV2(
                tenantId, actor.ActorType, actor.ActorId, action, TargetType, contactId.Value, now, retainUntil, metadata),
        };
    }
}
