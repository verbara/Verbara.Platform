using Verbara.Platform.Core;

namespace Verbara.Platform.Queues;

public sealed class ChannelCapacity
{
    public int MaxVoice { get; set; } = 1;
    public int MaxChat { get; set; } = 3;
    public int MaxEmail { get; set; } = 5;
    public int MaxSms { get; set; } = 3;

    /// <summary>
    /// W6 — the cap on the SUM of concurrently handled async channels (chat-pool +
    /// email + sms). Enforced SEPARATELY (not via <see cref="GetMax"/>, which is a
    /// strictly per-channel limit); the capacity service tallies async load across
    /// channels and rejects work that would push the combined count past this cap.
    /// </summary>
    public int MaxTotal { get; set; } = 5;

    public int GetMax(ChannelType channel) => channel switch
    {
        ChannelType.Voice => MaxVoice,
        // W6 — the whole chat family shares ONE pooled bucket vs MaxChat. Mapping every
        // chat sub-channel here keeps GetMax consistent with the capacity service's pooling
        // (which normalizes all of these to the WebChat bucket before calling GetMax).
        ChannelType.WhatsApp => MaxChat,
        ChannelType.WebChat => MaxChat,
        ChannelType.Messenger => MaxChat,
        ChannelType.Instagram => MaxChat,
        ChannelType.Telegram => MaxChat,
        ChannelType.Twitter => MaxChat,
        ChannelType.Video => MaxChat,
        ChannelType.Rcs => MaxChat,
        ChannelType.Sms => MaxSms,
        ChannelType.Email => MaxEmail,
        _ => 0,
    };
}
