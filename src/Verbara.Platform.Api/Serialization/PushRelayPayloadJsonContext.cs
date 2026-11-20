using System.Text.Json.Serialization;
using Verbara.Platform.Core;
using ProAnalytics = Verbara.Sdk.Pro.Analytics.Events;
using ProPush = Verbara.Sdk.Pro.Push.SignalR.Events;

namespace Verbara.Platform.Api.Serialization;

/// <summary>
/// Source-generated metadata for every push event that reaches Platform.Api's push bus and is NOT a cross-pod event.
/// The backplane relay forwards every event on the bus, so it serializes these payloads too; without their metadata
/// the serializer throws, the relay counts the event as dropped (<c>serialization_error</c>) and sends it with an
/// empty payload. No receiver decodes these types today; carrying a whole payload keeps the envelope truthful.
/// </summary>
/// <remarks>
/// Cross-pod events stay in <see cref="Verbara.Platform.Core.Push.PlatformPushJsonContext"/>, which both sides of the
/// backplane share and which must hold exactly the <see cref="ICrossPodEvent"/> set. The options must match that
/// context (default PascalCase names, nulls omitted). Pro types take a <c>Pro</c> property-name prefix because several
/// share a simple name with a Platform event. <c>PushRelayPayloadSerializationTests</c> fails when an event type that
/// can reach the bus is missing here.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// Platform events that stay on this process (not ICrossPodEvent).
[JsonSerializable(typeof(AgentCapacityChangedEvent))]
[JsonSerializable(typeof(CampaignDispositionSubmittedEvent))]
[JsonSerializable(typeof(CampaignMetricsUpdatedEvent))]
[JsonSerializable(typeof(CampaignStatusChangedEvent))]
[JsonSerializable(typeof(ConversationAbandonedEvent))]
[JsonSerializable(typeof(ConversationAssignedEvent))]
[JsonSerializable(typeof(ConversationMessageEvent))]
[JsonSerializable(typeof(ConversationOfferExpiredEvent))]
[JsonSerializable(typeof(ConversationOfferedEvent))]
[JsonSerializable(typeof(CsatResponseRecordedEvent))]
[JsonSerializable(typeof(NotificationEvent))]
[JsonSerializable(typeof(VoiceAgentHangupEvent))]
[JsonSerializable(typeof(VoiceScreenPopEvent))]
// Pro push events (the T27 bridges and the Pro packages Platform.Api hosts).
[JsonSerializable(typeof(ProAnalytics.AnalyticsIntervalCompletedEvent), TypeInfoPropertyName = "ProAnalyticsIntervalCompletedEvent")]
[JsonSerializable(typeof(ProAnalytics.LiveQueueStateEvent), TypeInfoPropertyName = "ProLiveQueueStateEvent")]
[JsonSerializable(typeof(ProAnalytics.QueueMetricsUpdatedEvent), TypeInfoPropertyName = "ProQueueMetricsUpdatedEvent")]
[JsonSerializable(typeof(ProPush.AgentCapacityChangedEvent), TypeInfoPropertyName = "ProAgentCapacityChangedEvent")]
[JsonSerializable(typeof(ProPush.AgentPresenceOfflineEvent), TypeInfoPropertyName = "ProAgentPresenceOfflineEvent")]
[JsonSerializable(typeof(ProPush.AgentPresenceOnlineEvent), TypeInfoPropertyName = "ProAgentPresenceOnlineEvent")]
[JsonSerializable(typeof(ProPush.AgentTenantMembershipChangedEvent), TypeInfoPropertyName = "ProAgentTenantMembershipChangedEvent")]
[JsonSerializable(typeof(ProPush.ClusterNodeDrainProgressEvent), TypeInfoPropertyName = "ProClusterNodeDrainProgressEvent")]
[JsonSerializable(typeof(ProPush.ClusterNodeStateChangedEvent), TypeInfoPropertyName = "ProClusterNodeStateChangedEvent")]
[JsonSerializable(typeof(ProPush.ConversationAssignedEvent), TypeInfoPropertyName = "ProConversationAssignedEvent")]
[JsonSerializable(typeof(ProPush.ConversationMessageEvent), TypeInfoPropertyName = "ProConversationMessageEvent")]
[JsonSerializable(typeof(ProPush.ConversationTransferredEvent), TypeInfoPropertyName = "ProConversationTransferredEvent")]
[JsonSerializable(typeof(ProPush.PresenceSnapshotEvent), TypeInfoPropertyName = "ProPresenceSnapshotEvent")]
internal sealed partial class PushRelayPayloadJsonContext : JsonSerializerContext;
