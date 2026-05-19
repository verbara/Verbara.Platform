using Verbara.Sdk.Push.Authz;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Platform.Realtime.Authz;

/// <summary>
/// Platform's <see cref="ISubscriptionAuthorizer"/> that gates push-bus
/// subscriptions using the same RBAC permission set that protects REST
/// endpoints in Platform.Api. Replaces the SDK default
/// <c>AllowAllSubscriptionAuthorizer</c>.
/// Moved from Verbara.Platform.Api in ADR-0022 Phase A.
/// </summary>
public sealed class RbacSubscriptionAuthorizer : ISubscriptionAuthorizer
{
    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
    {
        var topic = requestedPattern.ToString();

        if (subscriber.HasPermission("platform:all-tenants:read"))
            return AuthorizationResult.Allow();

        if (!string.IsNullOrEmpty(subscriber.UserId))
        {
            if (topic.StartsWith($"presence.agent.{subscriber.UserId}.", StringComparison.Ordinal)
                || topic.StartsWith($"agent.{subscriber.UserId}.", StringComparison.Ordinal))
            {
                return AuthorizationResult.Allow();
            }
        }

        var required = RequiredPermissionFor(topic);
        if (required is null)
            return AuthorizationResult.Deny($"Topic '{topic}' has no permission mapping.");

        return subscriber.HasPermission(required)
            ? AuthorizationResult.Allow()
            : AuthorizationResult.Deny($"Missing permission '{required}' for topic '{topic}'.");
    }

    private static string? RequiredPermissionFor(string topic) => topic switch
    {
        var t when t.StartsWith("conversation.", StringComparison.Ordinal) => "conversations:read",
        var t when t.StartsWith("agent.", StringComparison.Ordinal) => "agents:read",
        var t when t.StartsWith("presence.agent.", StringComparison.Ordinal) => "agents:read",
        var t when t.StartsWith("queue.", StringComparison.Ordinal) => "queues:read",
        var t when t.StartsWith("cluster.", StringComparison.Ordinal) => "cluster:read",
        var t when t.StartsWith("analytics.", StringComparison.Ordinal) => "analytics:read",
        var t when t.StartsWith("agentassist.", StringComparison.Ordinal) => "agentassist:read",
        var t when t.StartsWith("callanalytics.", StringComparison.Ordinal) => "callanalytics:read",
        var t when t.StartsWith("billing.", StringComparison.Ordinal) => "billing:read",
        var t when t.StartsWith("auth.session.", StringComparison.Ordinal) => "auth:sessions:read",
        var t when t.StartsWith("notification.", StringComparison.Ordinal) => "notifications:read",
        _ => null,
    };
}
