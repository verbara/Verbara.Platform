using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Realtime.Services;

internal static partial class RemoteEventDispatcherLog
{
    [LoggerMessage(EventId = 3100, Level = LogLevel.Debug,
        Message = "[REMOTE-DISPATCH] Decoded {EventType} from origin={OriginNodeId} tenant={TenantId}")]
    public static partial void Decoded(ILogger logger, string eventType, string? originNodeId, string tenantId);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Trace,
        Message = "[REMOTE-DISPATCH] No mapping registered for OriginalEventType={EventType}; skipping")]
    public static partial void UnknownEventType(ILogger logger, string eventType);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning,
        Message = "[REMOTE-DISPATCH] Empty RawPayload for {EventType} — Platform.Api missing PushProOptions.PayloadSerializerOptions?")]
    public static partial void EmptyPayload(ILogger logger, string eventType);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning,
        Message = "[REMOTE-DISPATCH] Failed to deserialise {EventType} payload: {Reason}")]
    public static partial void DeserialiseFailed(ILogger logger, string eventType, string reason);
}

/// <summary>
/// Hosted service that bridges the SDK Pro.Push.Redis envelope contract
/// (<see cref="RemotePushEvent"/>) into typed events the
/// <see cref="PushToHubRelay"/> can match with its <c>OfType&lt;T&gt;()</c>
/// subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> the SDK's
/// <c>Verbara.Sdk.Pro.Push.Backplane.RedisEventRelay</c> wraps every cross-node
/// event in a <see cref="RemotePushEvent"/> envelope on the receiving side —
/// [ADR-0025] explicitly rejects typed round-trip ("the concrete event subtype
/// lives in consumer assemblies and cannot be enumerated without reflection").
/// Consumers that need typed dispatch are expected to register their own
/// deserialiser. Without this bridge, the v2.4.7 dual-subscribe relay was a
/// no-op for cross-node fanout because the bus only ever contained
/// <see cref="RemotePushEvent"/>, never the typed Core/Pro records the relay's
/// <c>OfType&lt;T&gt;()</c> filters looked for.
/// </para>
/// <para>
/// <b>Wire format contract:</b> the envelope's <see cref="RemotePushEvent.OriginalEventType"/>
/// discriminator is the publishing event's <c>EventType</c> property
/// (e.g. <c>"agent.state_changed"</c> for
/// <see cref="AgentStateChangedEvent"/>). The dispatcher uses this discriminator
/// to choose a <c>JsonTypeInfo&lt;T&gt;</c> from the shared
/// <see cref="PlatformPushJsonContext"/> (also used by Platform.Api as its
/// <c>PushProOptions.PayloadSerializerOptions</c>) and deserialise
/// <see cref="RemotePushEvent.RawPayload"/> into the concrete record. The typed
/// event is then re-published to the local <see cref="IPushEventBus"/>, where
/// the relay's existing Core subscriptions (v2.4.7) pick it up.
/// </para>
/// <para>
/// <b>Loop safety:</b> <c>RedisEventRelay.ForwardToRedisAsync</c> short-circuits
/// when given a <see cref="RemotePushEvent"/>, so re-publishing the decoded
/// concrete type cannot echo back through the backplane (which would otherwise
/// fan out indefinitely across nodes).
/// </para>
/// </remarks>
internal sealed class RemoteEventDispatcher : IHostedService
{
    private readonly IPushEventBus _bus;
    private readonly ILogger<RemoteEventDispatcher> _logger;
    private IDisposable? _subscription;

    public RemoteEventDispatcher(IPushEventBus bus, ILogger<RemoteEventDispatcher> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.OfType<RemotePushEvent>().Subscribe(envelope =>
        {
            try
            {
                Dispatch(envelope);
            }
            catch (Exception ex)
            {
                RemoteEventDispatcherLog.DeserialiseFailed(_logger, envelope.OriginalEventType, ex.Message);
            }
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private void Dispatch(RemotePushEvent envelope)
    {
        if (envelope.RawPayload.Length == 0)
        {
            RemoteEventDispatcherLog.EmptyPayload(_logger, envelope.OriginalEventType);
            return;
        }

        // SINGLE SOURCE OF TRUTH for cross-pod decode. Adding a cross-pod event = add one entry
        // here AND a [JsonSerializable] line in PlatformPushJsonContext + the ICrossPodEvent marker.
        // CrossPodEventGuardTests asserts these three stay in lock-step (no silent message loss).
        if (Handlers.TryGetValue(envelope.OriginalEventType, out var typeInfo))
        {
            DecodeAndPublish(envelope, typeInfo);
        }
        else
        {
            // Not a Platform.Core event — let other consumers (Pro internal mergers, custom
            // subscribers, the Pro.Push SignalR Presence path) handle the RemotePushEvent directly.
            RemoteEventDispatcherLog.UnknownEventType(_logger, envelope.OriginalEventType);
        }
    }

    private static readonly FrozenDictionary<string, JsonTypeInfo> Handlers =
        new Dictionary<string, JsonTypeInfo>
        {
            ["agent.state_changed"] = PlatformPushJsonContext.Default.AgentStateChangedEvent,
            ["agent.pending_state_changed"] = PlatformPushJsonContext.Default.AgentPendingStateChangedEvent,
            ["conversation.state_changed"] = PlatformPushJsonContext.Default.ConversationStateChangedEvent,
            ["typification.submitted"] = PlatformPushJsonContext.Default.TypificationSubmittedEvent,
        }.ToFrozenDictionary();

    /// <summary>The CLR event types this dispatcher decodes cross-pod — derived from
    /// <see cref="Handlers"/> (single source of truth). The cross-pod guard test asserts this
    /// equals the set of <c>ICrossPodEvent</c> implementers and the PlatformPushJsonContext set.</summary>
    internal static readonly IReadOnlySet<Type> HandledEventTypes =
        Handlers.Values.Select(ti => ti.Type).ToFrozenSet();

    private void DecodeAndPublish(RemotePushEvent envelope, JsonTypeInfo typeInfo)
    {
        var json = System.Text.Encoding.UTF8.GetString(envelope.RawPayload);
        if (JsonSerializer.Deserialize(json, typeInfo) is not PushEvent decoded)
        {
            RemoteEventDispatcherLog.DeserialiseFailed(_logger, envelope.OriginalEventType, "JsonSerializer returned null");
            return;
        }

        // Carry the envelope metadata through so tenant/user routing keeps working.
        if (envelope.Metadata is not null)
        {
            decoded = decoded with { Metadata = envelope.Metadata };
        }

        RemoteEventDispatcherLog.Decoded(_logger, envelope.OriginalEventType, envelope.SourceNodeId, envelope.Metadata?.TenantId ?? "(none)");

        var pending = _bus.PublishAsync(decoded, CancellationToken.None);
        if (!pending.IsCompletedSuccessfully)
        {
            pending.AsTask().GetAwaiter().GetResult();
        }
    }
}
