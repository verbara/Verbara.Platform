using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Events;

namespace Asterisk.Platform.Core;

/// <summary>
/// Platform-specific event delivery filter used by SSE and other Sdk.Push subscribers.
/// Enforces the same semantics as <see cref="DefaultDeliveryFilter"/>:
/// <list type="bullet">
///   <item>Tenant isolation: events are never delivered across tenants.</item>
///   <item>User targeting: events whose <see cref="PushEventMetadata.UserId"/> is set are
///     delivered only to the matching subscriber (e.g. <c>NotificationEvent</c>).</item>
///   <item>Tenant broadcasts: events without a target user reach every subscriber in the tenant.</item>
/// </list>
/// Registered after <c>AddAsteriskPush()</c> so it overrides the SDK default and gives
/// the platform a single hook for any future Platform-specific delivery rules
/// (per-role gating, per-permission gating, etc.).
/// </summary>
public sealed class PlatformDeliveryFilter : IEventDeliveryFilter
{
    public bool IsDeliverableToSubscriber(PushEvent pushEvent, SubscriberContext subscriber)
    {
        ArgumentNullException.ThrowIfNull(pushEvent);
        ArgumentNullException.ThrowIfNull(subscriber);

        // 1) Tenant isolation.
        if (!string.Equals(pushEvent.Metadata.TenantId, subscriber.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        // 2) Tenant-scoped broadcast: deliver to every subscriber in the tenant.
        if (pushEvent.Metadata.UserId is null)
        {
            return true;
        }

        // 3) User-targeted event (e.g. NotificationEvent): subscriber must match.
        if (subscriber.UserId is null)
        {
            return false;
        }

        return string.Equals(pushEvent.Metadata.UserId, subscriber.UserId, StringComparison.Ordinal);
    }
}
