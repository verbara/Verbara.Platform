using System.Text.Json.Serialization;

namespace Verbara.Platform.Core.Push;

/// <summary>
/// AOT-clean <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
/// that knows every <c>Verbara.Platform.Core</c> push-event record carried by
/// <c>Pro.Push.Redis</c> backplane envelopes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in <c>Verbara.Platform.Core</c>:</b> the same context is
/// consumed by BOTH the publishing side (Verbara.Platform.Api configures it as
/// <c>PushProOptions.PayloadSerializerOptions</c> so <c>RedisEventRelay</c>
/// serialises every cross-node event's payload bytes) AND the receiving side
/// (Verbara.Platform.Realtime <c>RemoteEventDispatcher</c> deserialises
/// <c>RemotePushEvent.RawPayload</c> using the same TypeInfoResolver). Sharing
/// a single context guarantees the two sides stay in lock-step on field shapes
/// — the consequence of skew is silent message loss (payload deserialises to
/// the wrong shape and the dispatcher's typed re-publish never fires).
/// </para>
/// <para>
/// <b>Why source-gen vs reflection:</b> Verbara.Platform.Api publishes Native
/// AOT (ADR-0022 Phase D). Without a source-gen TypeInfoResolver, the AOT
/// compiler would trim the field metadata <c>JsonSerializer.Serialize(obj,
/// objType, options)</c> needs, and runtime serialisation would fail with
/// <c>NotSupportedException</c> at the first publish.
/// </para>
/// <para>
/// <b>Adding a new event:</b> add a <c>[JsonSerializable(typeof(YourEvent))]</c>
/// line below AND register the matching mapping in
/// <c>Verbara.Platform.Realtime.Services.RemoteEventDispatcher.RegisterMappings()</c>.
/// Failure to do both will surface as a Test 5 harness regression (or
/// silent message loss in production).
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentStateChangedEvent))]
[JsonSerializable(typeof(ConversationStateChangedEvent))]
public partial class PlatformPushJsonContext : JsonSerializerContext;
