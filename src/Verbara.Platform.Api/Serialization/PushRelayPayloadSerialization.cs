using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Verbara.Platform.Api.Serialization;

/// <summary>
/// Builds the <see cref="JsonSerializerOptions"/> that Platform.Api hands to the push backplane relay
/// (<c>PushProOptions.PayloadSerializerOptions</c>). The relay serializes the payload of every event on this
/// process's push bus with <c>JsonSerializer.Serialize(event, event.GetType(), options)</c>, so the resolver must
/// know every event type that reaches the bus: <see cref="Verbara.Platform.Core.Push.PlatformPushJsonContext"/> for the
/// cross-pod events, <see cref="PushRelayPayloadJsonContext"/> for the rest.
/// </summary>
internal static class PushRelayPayloadSerialization
{
    /// <summary>Creates the options the relay serializes event payloads with.</summary>
    public static JsonSerializerOptions CreateOptions() => new()
    {
        // Cross-pod events first: their contract is the one the receiving side decodes with.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Verbara.Platform.Core.Push.PlatformPushJsonContext.Default,
            PushRelayPayloadJsonContext.Default),
    };
}
