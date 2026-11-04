using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Endpoints.Shared;

/// <summary>
/// Which content-bearing events a live stream receives (contact-history-opens-by-assignment, design D17; Platform/ADR-0047).
/// <c>SseEndpoints.StreamEvents</c> applies it after <see cref="PlatformDeliveryFilter"/>, which keeps tenant isolation
/// and user targeting, with the subscriber's <see cref="ConversationReaderClass"/> resolved once when the stream opens.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An event that is not an <see cref="IConversationContentEvent"/> passes, whoever the subscriber is.</item>
/// <item>A content event reaches a subscriber that reads every conversation (monitor or read, or Admin/SystemAdmin:
/// <see cref="ConversationReaderClass.ReadsAll"/>, the same definition as the REST read rule), or a handle holder whose
/// Agent profile is in the event's <see cref="IConversationContentEvent.Audience"/>, and nobody else: not an anonymous or
/// unscoped stream, not a handle holder without an Agent profile.</item>
/// </list>
/// Pure and allocation-free: one type test and at most two string comparisons per event and subscriber, no store read.
/// </remarks>
internal static class ConversationContentDelivery
{
    /// <summary>
    /// Whether <paramref name="evt"/> may reach the stream of <paramref name="reader"/>; <see langword="null"/> is a stream
    /// that could not be classified (anonymous or unscoped), which receives no content event.
    /// </summary>
    public static bool IsDeliverable(PlatformEvent evt, ConversationReaderClass? reader) =>
        evt is not IConversationContentEvent
        || (reader is not null && IsDeliverable(evt, reader.ReadsAll, reader.HandlingAgentId));

    /// <summary>
    /// Whether <paramref name="evt"/> may reach a subscriber that <paramref name="readsAll"/>, or that handles
    /// conversations as the Agent profile <paramref name="handlingAgentId"/> (<see langword="null"/> when it does not).
    /// </summary>
    public static bool IsDeliverable(PlatformEvent evt, bool readsAll, EntityId? handlingAgentId)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (evt is not IConversationContentEvent content || readsAll)
            return true;

        return handlingAgentId is { } agentId && content.Audience.Includes(agentId.Value);
    }
}
