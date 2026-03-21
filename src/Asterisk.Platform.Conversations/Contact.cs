using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

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

    public ChannelAddress? FindAddress(ChannelType channel) =>
        _addresses.Find(a => a.Channel == channel);

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
}
