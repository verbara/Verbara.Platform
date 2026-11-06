using System.Security.Claims;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Who changed a contact's do-not-contact or consent (contacts-writes-follow-their-permissions, design D17): the id
/// written to <see cref="Contact.UpdatedBy"/> and the actor of the change's audit record.
/// </summary>
/// <param name="ActorId">The user id, the API key id for a key bound to no user, or <c>system</c>.</param>
/// <param name="ActorType"><see cref="UserType"/>, <see cref="ApiKeyType"/> or <see cref="SystemType"/>.</param>
internal sealed record ContactActor(string ActorId, string ActorType)
{
    /// <summary>
    /// The impersonation context of the caller (<see cref="CallerIdentity.AddImpersonationContext"/>): under
    /// impersonation <see cref="ActorId"/> is the impersonating admin's, from another tenant, so the record also names
    /// that tenant and the impersonation session. Empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, string> Impersonation { get; init; } = Empty;

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A signed-in user, or an API key acting as the user it is bound to.</summary>
    public const string UserType = "user";

    /// <summary>An API key bound to no user.</summary>
    public const string ApiKeyType = "api_key";

    /// <summary>The platform itself, with no caller.</summary>
    public const string SystemType = "system";

    /// <summary>The platform acting with no caller: <c>system</c>/<c>system</c>.</summary>
    public static ContactActor System { get; } = new("system", SystemType);

    /// <summary>
    /// The actor of an HTTP caller. The id is <see cref="CallerIdentity.ResolveUserId"/>: a JWT user's id, the owner of
    /// a bound API key, or the key id of a key bound to no user. The type is <see cref="ApiKeyType"/> for a principal of
    /// the API-key scheme with no <c>user_id</c>, else <see cref="UserType"/>. Under impersonation it carries the
    /// impersonation context, which every record it writes adds to its metadata.
    /// </summary>
    public static ContactActor Of(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var unboundKey = caller.FindFirstValue("user_id") is null
            && caller.Identities.Any(identity =>
                identity.IsAuthenticated
                && string.Equals(identity.AuthenticationType, AuthSchemeConfiguration.ApiKeyScheme, StringComparison.Ordinal));
        var impersonation = new Dictionary<string, string>(StringComparer.Ordinal);
        CallerIdentity.AddImpersonationContext(impersonation, caller);
        return new ContactActor(CallerIdentity.ResolveUserIdOrSystem(caller), unboundKey ? ApiKeyType : UserType)
        {
            Impersonation = impersonation,
        };
    }
}

/// <summary>
/// Records a contact's opt-out (design D16). It only restricts: it can mark the contact do-not-contact and record
/// <c>false</c> for channels, never clear the mark and never record <c>true</c>. The route
/// <c>POST /contacts/{id}/opt-out</c> wraps it, and a caller with no HTTP request passes <see cref="ContactActor.System"/>.
/// </summary>
internal interface IContactOptOutService
{
    /// <summary>
    /// Marks the contact do-not-contact when <paramref name="doNotContact"/> is <see langword="true"/> and records
    /// <c>false</c> for each of <paramref name="channels"/>; a channel not named keeps its value. When something
    /// changes, sets <see cref="Contact.UpdatedAt"/> and <see cref="Contact.UpdatedBy"/> (the actor's id) and writes the
    /// contact together with one <c>contact.opted_out</c> audit record attributed to the actor (design D17); a failure
    /// of either write leaves both unwritten and throws. When nothing would change, writes nothing.
    /// </summary>
    /// <returns>The contact as it now stands, or <see langword="null"/> when the tenant has no contact with that id.</returns>
    Task<Contact?> OptOutAsync(
        TenantId tenantId,
        EntityId contactId,
        bool doNotContact,
        IReadOnlyCollection<ChannelType> channels,
        ContactActor actor,
        CancellationToken ct);
}

/// <inheritdoc />
internal sealed class ContactOptOutService(IContactStore contacts, IContactAuditWriter writer, IClock clock) : IContactOptOutService
{
    /// <inheritdoc />
    public async Task<Contact?> OptOutAsync(
        TenantId tenantId,
        EntityId contactId,
        bool doNotContact,
        IReadOnlyCollection<ChannelType> channels,
        ContactActor actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(actor);

        var contact = await contacts.GetByIdAsync(tenantId, contactId, ct);
        if (contact is null)
            return null;

        var markDoNotContact = doNotContact && !contact.DoNotContact;
        var recordNo = channels
            .Distinct()
            .Where(channel => !(contact.ChannelConsent.TryGetValue(channel, out var consented) && !consented))
            .ToList();
        if (!markDoNotContact && recordNo.Count == 0)
            return contact;

        var before = ContactAuditRecord.StateOf(contact);
        if (markDoNotContact)
            contact.DoNotContact = true;
        foreach (var channel in recordNo)
            contact.SetConsent(channel, false);
        var now = clock.UtcNow;
        contact.UpdatedAt = now;
        contact.UpdatedBy = actor.ActorId;
        var record = ContactAuditRecord.Change(
            tenantId, ContactAuditRecord.OptedOutAction, actor, contact.ContactId, before, ContactAuditRecord.StateOf(contact), now);
        await writer.SaveAsync(contact, record, ct);
        return contact;
    }
}
