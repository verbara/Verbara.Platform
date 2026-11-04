namespace Verbara.Platform.Core;

/// <summary>
/// The sentinel channel addresses that make a contact <b>shared</b>: one contact the platform resolves for many
/// different callers, because the caller gave nothing that identifies them (contact-history-opens-by-assignment,
/// design D18; Platform/ADR-0047 Decision 5). A contact holding one of these never opens other callers' conversations
/// to an agent: the read rule's contact branch does not apply to it, and its conversation list holds only the
/// caller's own conversations.
/// </summary>
/// <remarks>
/// Every entry is the voice caller with a withheld number (CLIR), which the voice bridge resolves to the tenant's one
/// <c>anonymous</c> voice contact (<see cref="VoiceCallerAddressOf"/>). The other entries are the placeholder strings a
/// withheld number arrives as instead of an empty caller id: Asterisk's AMI reports a missing one as the literal
/// <c>&lt;unknown&gt;</c>, and carriers send <c>Restricted</c>, <c>Unavailable</c>, <c>Private</c> or <c>unknown</c>.
/// The bridge maps them to <c>anonymous</c>; they stay in the list so that a contact a build before that mapping
/// resolved from one of them is still treated as shared. Every other channel keys its contact on a per-sender id (the
/// design's sweep). A new sentinel is one more entry here, used by the code that resolves it, so the address and the
/// exclusion cannot drift.
/// </remarks>
public static class SharedContactAddresses
{
    /// <summary>The voice address of every caller whose number is withheld or empty.</summary>
    public const string AnonymousVoiceCaller = "anonymous";

    /// <summary>The withheld-caller-id voice contact's address.</summary>
    public static ChannelAddress AnonymousVoice { get; } = new(ChannelType.Voice, AnonymousVoiceCaller);

    /// <summary>
    /// The caller id numbers that identify no caller: <see cref="AnonymousVoiceCaller"/> first, then the placeholders
    /// Asterisk and carriers send for a withheld number. Compared ignoring case and surrounding white space.
    /// </summary>
    private static readonly string[] WithheldVoiceCallerIds =
        [AnonymousVoiceCaller, "<unknown>", "unknown", "restricted", "unavailable", "private"];

    /// <summary>Every sentinel address.</summary>
    public static IReadOnlyList<ChannelAddress> All { get; } =
        [.. WithheldVoiceCallerIds.Select(id => new ChannelAddress(ChannelType.Voice, id))];

    /// <summary>
    /// The voice address the bridge resolves a caller's contact from: <see cref="AnonymousVoiceCaller"/> when
    /// <paramref name="callerIdNum"/> is empty or a withheld placeholder, else the number unchanged.
    /// </summary>
    public static string VoiceCallerAddressOf(string? callerIdNum) =>
        string.IsNullOrWhiteSpace(callerIdNum) || IsWithheldVoiceCallerId(callerIdNum) ? AnonymousVoiceCaller : callerIdNum;

    /// <summary>Whether <paramref name="address"/> is a sentinel: the same channel, the address compared ignoring case.</summary>
    public static bool IsShared(ChannelAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Channel == ChannelType.Voice && IsWithheldVoiceCallerId(address.Address);
    }

    /// <summary>Whether any of <paramref name="addresses"/> (a contact's addresses) is a sentinel.</summary>
    public static bool IsShared(IEnumerable<ChannelAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return addresses.Any(IsShared);
    }

    private static bool IsWithheldVoiceCallerId(string? callerId)
    {
        if (callerId is null)
            return false;

        var trimmed = callerId.AsSpan().Trim();
        foreach (var withheld in WithheldVoiceCallerIds)
        {
            if (trimmed.Equals(withheld, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
