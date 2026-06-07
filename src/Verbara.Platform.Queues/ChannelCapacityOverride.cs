namespace Verbara.Platform.Queues;

/// <summary>
/// Sparse per-agent capacity override. Each null field means "inherit the tenant default".
/// Resolved to an effective <see cref="ChannelCapacity"/> at read time (see IAgentCapacityResolver, W6).
/// </summary>
public sealed class ChannelCapacityOverride
{
    public int? MaxVoice { get; set; }
    public int? MaxChat { get; set; }
    public int? MaxEmail { get; set; }
    public int? MaxSms { get; set; }
    public int? MaxTotal { get; set; }

    /// <summary>Merge this override over <paramref name="defaults"/> field-by-field (override wins when non-null).</summary>
    public ChannelCapacity ToEffective(ChannelCapacity defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return new ChannelCapacity
        {
            MaxVoice = MaxVoice ?? defaults.MaxVoice,
            MaxChat = MaxChat ?? defaults.MaxChat,
            MaxEmail = MaxEmail ?? defaults.MaxEmail,
            MaxSms = MaxSms ?? defaults.MaxSms,
            MaxTotal = MaxTotal ?? defaults.MaxTotal,
        };
    }
}
