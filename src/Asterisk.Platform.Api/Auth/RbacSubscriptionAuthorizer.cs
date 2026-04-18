using Asterisk.Sdk.Push.Authz;
using Asterisk.Sdk.Push.Delivery;
using Asterisk.Sdk.Push.Topics;

namespace Asterisk.Platform.Api.Auth;

/// <summary>
/// Platform's <see cref="ISubscriptionAuthorizer"/> that gates push-bus
/// subscriptions using the same RBAC permission set that protects REST
/// endpoints. Replaces the SDK default <c>AllowAllSubscriptionAuthorizer</c>.
/// </summary>
/// <remarks>
/// Mapping table (excerpted from the v1.4.0 spec):
/// <list type="bullet">
///   <item><c>conversations:read</c> → <c>conversation.*</c></item>
///   <item><c>agents:read</c> → <c>agent.*</c> and <c>presence.agent.*</c></item>
///   <item><c>queues:read</c> → <c>queue.*</c></item>
///   <item><c>cluster:read</c> → <c>cluster.*</c></item>
///   <item><c>analytics:read</c> → <c>analytics.*</c></item>
///   <item><c>agentassist:read</c> → <c>agentassist.*</c></item>
///   <item><c>callanalytics:read</c> → <c>callanalytics.*</c></item>
///   <item><c>billing:read</c> → <c>billing.*</c></item>
///   <item><c>auth:sessions:read</c> → <c>auth.session.*</c></item>
///   <item><c>notifications:read</c> → <c>notification.*</c></item>
///   <item><c>platform:all-tenants:read</c> → <c>**</c> (cross-tenant)</item>
///   <item>
///     <em>Implicit</em>: an agent is always permitted to subscribe to
///     <c>presence.agent.{UserId}.*</c> and <c>agent.{UserId}.*</c> where
///     <c>{UserId}</c> is the JWT subject.
///   </item>
/// </list>
/// <para>
/// Permissions are supplied by <see cref="SubscriberContext.Permissions"/>,
/// resolved upstream by the SDK's delivery filter pipeline — this authorizer
/// performs only the topic-prefix match, no resolver calls.
/// </para>
/// </remarks>
public sealed class RbacSubscriptionAuthorizer : ISubscriptionAuthorizer
{
    /// <inheritdoc />
    public AuthorizationResult CanSubscribe(SubscriberContext subscriber, TopicPattern requestedPattern)
    {
        var topic = requestedPattern.ToString();

        // Cross-tenant super permission bypasses all other checks.
        if (subscriber.HasPermission("platform:all-tenants:read"))
            return AuthorizationResult.Allow();

        // Implicit self-access for the authenticated user.
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
