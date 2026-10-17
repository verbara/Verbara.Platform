namespace Verbara.Platform.Core;

/// <summary>
/// Marks a <see cref="PlatformEvent"/> that every Api replica publishes from shared state (for example
/// a poll of the shared database), so each replica's SSE clients get it from their own replica's
/// publisher, while an effect outside the process is taken by one replica only: the webhook dispatcher
/// creates deliveries for a marked event only on the replica that holds the event's lease
/// (<c>CampaignMetricsLeaderResources.Webhooks</c> for <see cref="CampaignMetricsUpdatedEvent"/>).
/// The interface changes neither the event's JSON (source-generated from its properties) nor the
/// cross-pod set (keyed on <see cref="ICrossPodEvent"/>). The check is a plain type test; no
/// reflection ships.
/// </summary>
public interface IPublishedOnEveryReplica;
