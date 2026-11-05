using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services;

/// <summary>The contact a WebChat session resolved, and how far that session's identity is known.</summary>
internal sealed record WebChatSessionIdentity(Contact Contact, ContactIdentityLevel Level);

/// <summary>
/// Resolves the one contact of a new WebChat session: by the customer id a verified token names (<c>Verified</c>), else
/// by the browser's key (<c>Unverified</c>), else a new contact of this session alone (<c>Anonymous</c>). A found
/// contact is written only by targeted updates, so an edit made after it was read survives and a contact erased since
/// then stays erased. Form data and a token's profile claims only fill display data: they never find a contact.
/// </summary>
internal sealed partial class WebChatSessionIdentityResolver(
    IContactStore contacts,
    IConversationStore conversations,
    IClock clock,
    ILogger<WebChatSessionIdentityResolver> logger)
{
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;

    // One person rarely has this many WebChat conversations open at once, because a session that never sends a message
    // is abandoned by the queue timeout. This many means many browsers share the key. The count and the conversation
    // insert are separate steps, so a burst can overshoot it by the number in flight; the session rate limit bounds that.
    private const int SaturatedOpenConversations = 5;

    private const string SaturatedCode = "webchat-visitor-key-saturated";

    // A second try covers a contact erased between its find and the write: the key then finds nothing and inserts.
    private const int ResolveAttempts = 2;

    private static readonly TimeSpan s_saturationWarningInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<TenantId, DateTimeOffset> _lastSaturationWarning = new();

    /// <summary>
    /// Resolves the contact of a session whose own address is <paramref name="sessionAddress"/>. <paramref name="claims"/>
    /// are those of a token that verified, and <paramref name="visitorId"/>, the browser's id, is used only without them.
    /// </summary>
    public async Task<WebChatSessionIdentity> ResolveAsync(
        TenantId tenantId,
        ChannelAddress sessionAddress,
        WebChatIdentityClaims? claims,
        string? visitorId,
        string? visitorName,
        string? visitorEmail,
        CancellationToken ct)
    {
        var name = CleanName(visitorName);
        var email = CleanEmail(visitorEmail);

        if (claims is not null)
        {
            var key = new ChannelAddress(ChannelType.WebChat, ChannelAddress.WebChatExternalKeyPrefix + claims.ExternalId);
            var verified = await ResolveByKeyAsync(
                tenantId, key, sessionAddress, CleanName(claims.Name) ?? name, CleanEmail(claims.Email) ?? email, isLevel1: false, ct);
            if (verified is not null)
                return new WebChatSessionIdentity(verified, ContactIdentityLevel.Verified);
        }
        else if (WebChatVisitorKey.TryCreate(visitorId, out var key))
        {
            var unverified = await ResolveByKeyAsync(tenantId, key, sessionAddress, name, email, isLevel1: true, ct);
            if (unverified is not null)
                return new WebChatSessionIdentity(unverified, ContactIdentityLevel.Unverified);
        }

        var own = NewContact(tenantId, sessionAddress, key: null, name, email);
        await contacts.SaveAsync(own, ct);
        return new WebChatSessionIdentity(own, ContactIdentityLevel.Anonymous);
    }

    // Null when the key cannot take this session, so the session falls back to a contact of its own.
    private async Task<Contact?> ResolveByKeyAsync(
        TenantId tenantId,
        ChannelAddress key,
        ChannelAddress sessionAddress,
        string? name,
        string? email,
        bool isLevel1,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < ResolveAttempts; attempt++)
        {
            var candidate = NewContact(tenantId, sessionAddress, key, name, email);
            var held = await contacts.FindOrInsertByAddressAsync(tenantId, key, candidate, ct);
            if (held.ContactId == candidate.ContactId)
                return held;

            if (isLevel1 && !await CanTakeAnotherLevel1SessionAsync(tenantId, held, ct))
                return null;

            var attached = await contacts.AttachWebChatSessionAsync(
                tenantId, held.ContactId, sessionAddress, name, clock.UtcNow, DefaultContactIdentityResolver.SystemActorId, ct);
            if (attached is null)
                continue;

            if (email is null || attached.CustomFields.ContainsKey(Contact.WebChatEmailField))
                return attached;

            return await contacts.SetCustomFieldIfAbsentAsync(
                       tenantId, held.ContactId, Contact.WebChatEmailField, email, clock.UtcNow, DefaultContactIdentityResolver.SystemActorId, ct)
                   ?? attached;
        }

        return null;
    }

    private async Task<bool> CanTakeAnotherLevel1SessionAsync(TenantId tenantId, Contact found, CancellationToken ct)
    {
        // A browser key must not reach a contact a token vouched for. Such a contact only exists if someone typed the
        // address by hand before the contact routes refused it.
        if (found.Addresses.Any(a => a.IsWebChatExternalKey()))
            return false;

        var open = await conversations.CountOpenByContactAsync(tenantId, found.ContactId, ChannelType.WebChat, ct);
        if (open < SaturatedOpenConversations)
            return true;

        WarnSaturated(tenantId);
        return false;
    }

    private void WarnSaturated(TenantId tenantId)
    {
        var now = clock.UtcNow;
        var warnedNow = _lastSaturationWarning.TryGetValue(tenantId, out var last)
            ? now - last >= s_saturationWarningInterval && _lastSaturationWarning.TryUpdate(tenantId, now, last)
            : _lastSaturationWarning.TryAdd(tenantId, now);
        if (warnedNow)
            LogKeySaturated(logger, tenantId.Value, SaturatedCode);
    }

    private Contact NewContact(TenantId tenantId, ChannelAddress sessionAddress, ChannelAddress? key, string? name, string? email)
    {
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = name,
            CreatedAt = clock.UtcNow,
        };
        if (key is not null)
            contact.AddAddress(key);
        contact.AddAddress(sessionAddress);
        if (email is not null)
            contact.SetCustomField(Contact.WebChatEmailField, email);
        return contact;
    }

    private static string? CleanName(string? value)
    {
        var name = Clean(value);
        if (name is null || name.Length <= MaxNameLength)
            return name;

        // Never between the two halves of a character outside the BMP: the cut would not be valid text.
        return name[..(char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)];
    }

    private static string? CleanEmail(string? value)
    {
        var email = Clean(value);
        return email is { Length: > MaxEmailLength } ? null : email;
    }

    // PostgreSQL's text and jsonb cannot hold U+0000, so it never reaches the store.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebChat visitor key shared by too many open conversations for tenant {TenantId}: {Code}")]
    private static partial void LogKeySaturated(ILogger logger, string tenantId, string code);
}
