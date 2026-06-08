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
// v2.5.1: keep PropertyNamingPolicy DEFAULT (PascalCase). The previous
// CamelCase setting was ignored by `JsonSerializer.Serialize(obj, runtimeType,
// options)` — the 3-arg overload SDK Pro.Push.Redis.RedisEventRelay calls in
// SerializePayload(). Result: serialised JSON came out PascalCase (driven by
// options' identity naming policy) while the deserialise side via TypeInfo
// used CamelCase → every field arrived null on the receiver. PascalCase on
// both ends is the SDK-overload-safe contract; the SDK doesn't care about
// naming, only consistency.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentStateChangedEvent))]
[JsonSerializable(typeof(AgentPendingStateChangedEvent))]
[JsonSerializable(typeof(ConversationStateChangedEvent))]
[JsonSerializable(typeof(TypificationSubmittedEvent))]
public partial class PlatformPushJsonContext : JsonSerializerContext;
