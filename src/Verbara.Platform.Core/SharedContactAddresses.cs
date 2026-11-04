namespace Verbara.Platform.Core;

/// <summary>
/// The sentinel channel addresses that make a contact <b>shared</b>: one contact the platform resolves for many
/// different callers, because the caller gave nothing that identifies them (contact-history-opens-by-assignment,
/// design D18; Platform/ADR-0047 Decision 5). A contact holding one of these never opens other callers' conversations
/// to an agent: the read rule's contact branch does not apply to it, and its conversation list holds only the
/// caller's own conversations.
/// </summary>
/// <remarks>
/// Today the only entry is the voice caller with a withheld number (CLIR), which the voice bridge resolves to the
/// tenant's one <c>anonymous</c> voice contact. Every other channel keys its contact on a per-sender id (the design's
/// sweep). A new sentinel is one more entry here, used by the code that resolves it, so the address and the exclusion
/// cannot drift.
/// </remarks>
public static class SharedContactAddresses
{
    /// <summary>The voice address of every caller whose number is withheld or empty.</summary>
    public const string AnonymousVoiceCaller = "anonymous";

    /// <summary>The withheld-caller-id voice contact's address.</summary>
    public static ChannelAddress AnonymousVoice { get; } = new(ChannelType.Voice, AnonymousVoiceCaller);

    /// <summary>Every sentinel address.</summary>
    public static IReadOnlyList<ChannelAddress> All { get; } = [AnonymousVoice];

    /// <summary>Whether <paramref name="address"/> is a sentinel: the same channel, the address compared ignoring case.</summary>
    public static bool IsShared(ChannelAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        foreach (var sentinel in All)
        {
            if (sentinel.Channel == address.Channel
                && string.Equals(sentinel.Address, address.Address, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="addresses"/> (a contact's addresses) is a sentinel.</summary>
    public static bool IsShared(IEnumerable<ChannelAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return addresses.Any(IsShared);
    }
}
