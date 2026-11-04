using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed class Contact : ITenantScoped, IAuditable
{
    public required EntityId ContactId { get; init; }
    public required TenantId TenantId { get; init; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Company { get; set; }
    public string? Segment { get; set; }
    public ChannelType? PreferredChannel { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? Timezone { get; set; }
    public bool DoNotContact { get; set; }

    private readonly List<ChannelAddress> _addresses = [];
    public IReadOnlyList<ChannelAddress> Addresses => _addresses;

    private readonly Dictionary<string, string> _customFields = new();
    public IReadOnlyDictionary<string, string> CustomFields => _customFields;

    private readonly Dictionary<ChannelType, bool> _channelConsent = new();
    public IReadOnlyDictionary<ChannelType, bool> ChannelConsent => _channelConsent;

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }

    public void AddAddress(ChannelAddress address)
    {
        if (!_addresses.Contains(address))
            _addresses.Add(address);
    }

    public void RemoveAddress(ChannelAddress address) =>
        _addresses.Remove(address);

    /// <summary>
    /// The address a message to this contact on <paramref name="channel"/> is sent to. A WhatsApp
    /// business-scoped user id is an identity, not a destination (the Cloud API cannot send to it yet), so it is
    /// never returned: a contact known only by one has no WhatsApp address to send to. A WebChat identity key is
    /// skipped for the same reason.
    /// </summary>
    public ChannelAddress? FindAddress(ChannelType channel) =>
        _addresses.Find(a => a.Channel == channel && !a.IsWhatsAppBusinessScopedUserId() && !a.IsWebChatIdentityKey());

    public void SetCustomField(string key, string value) =>
        _customFields[key] = value;

    public void SetConsent(ChannelType channel, bool consented) =>
        _channelConsent[channel] = consented;

    public bool HasConsent(ChannelType channel) =>
        _channelConsent.TryGetValue(channel, out var c) && c;

    public string? FullName =>
        (FirstName, LastName) switch
        {
            (not null, not null) => $"{FirstName} {LastName}",
            (not null, null) => FirstName,
            (null, not null) => LastName,
            _ => null,
        };

    /// <summary>
    /// The contact's WebChat identity level, computed from its addresses, so it cannot drift from the keys that
    /// produce it. <see langword="null"/> for a contact with no WebChat address.
    /// </summary>
    public ContactIdentityLevel? IdentityLevel
    {
        get
        {
            ContactIdentityLevel? level = null;
            foreach (var address in _addresses)
            {
                if (address.IsWebChatExternalKey())
                    return ContactIdentityLevel.Verified;
                if (address.IsWebChatVisitorKey())
                    level = ContactIdentityLevel.Unverified;
                else if (address.Channel == ChannelType.WebChat)
                    level ??= ContactIdentityLevel.Anonymous;
            }

            return level;
        }
    }
}
