using Verbara.Platform.Realtime.Authz;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Topics;

namespace Verbara.Platform.Realtime.Tests.Authz;

/// <summary>
/// Unit tests for the topic-prefix → permission mapping that gates
/// push-bus subscriptions against Verbara Platform's RBAC permission set.
/// Mirrors the contract exercised by the SDK Push delivery filter pipeline
/// when SignalR clients subscribe to Hub topics.
/// </summary>
public sealed class RbacSubscriptionAuthorizerTests
{
    private static SubscriberContext Subscriber(string userId, params string[] permissions) =>
        new(
            TenantId: "tenant-1",
            UserId: userId,
            Roles: new HashSet<string>(),
            Permissions: new HashSet<string>(permissions));

    private readonly RbacSubscriptionAuthorizer _sut = new();

    [Theory]
    [InlineData("conversation.assigned", "conversations:read")]
    [InlineData("agent.42.state-changed", "agents:read")]
    [InlineData("queue.q-1.size", "queues:read")]
    [InlineData("cluster.node-1.health", "cluster:read")]
    [InlineData("analytics.live.wallboard", "analytics:read")]
    [InlineData("agentassist.session-42.suggestion", "agentassist:read")]
    [InlineData("callanalytics.session-42.score", "callanalytics:read")]
    [InlineData("billing.invoice.issued", "billing:read")]
    [InlineData("auth.session.revoked", "auth:sessions:read")]
    [InlineData("notification.user.X", "notifications:read")]
    public void CanSubscribe_ShouldAllow_WhenPermissionMatchesPrefix(string topic, string permission)
    {
        var subscriber = Subscriber("user-1", permission);
        var pattern = TopicPattern.Parse(topic);

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeTrue($"permission '{permission}' covers topic prefix '{topic}'");
    }

    [Fact]
    public void CanSubscribe_ShouldDeny_WhenPermissionMissing()
    {
        var subscriber = Subscriber("user-1"); // no permissions
        var pattern = TopicPattern.Parse("conversation.assigned");

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("conversations:read");
    }

    [Fact]
    public void CanSubscribe_ShouldAllowCrossTenant_WhenPlatformAllTenantsReadPermissionPresent()
    {
        var subscriber = Subscriber("admin", "platform:all-tenants:read");
        var pattern = TopicPattern.Parse("anything.under.any.tenant");

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void CanSubscribe_ShouldAllow_WhenSubscribingToOwnPresenceTopic()
    {
        var subscriber = Subscriber("user-42"); // no permissions
        var pattern = TopicPattern.Parse("presence.agent.user-42.online");

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeTrue("agents are always allowed to subscribe to their own presence");
    }

    [Fact]
    public void CanSubscribe_ShouldAllow_WhenSubscribingToOwnAgentTopic()
    {
        var subscriber = Subscriber("user-7"); // no permissions
        var pattern = TopicPattern.Parse("agent.user-7.state");

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeTrue("agents are always allowed to subscribe to their own agent.* topic");
    }

    [Fact]
    public void CanSubscribe_ShouldDeny_WhenTopicHasNoMappedPermission()
    {
        var subscriber = Subscriber("user-1", "conversations:read", "agents:read");
        var pattern = TopicPattern.Parse("unknown.topic.prefix");

        var result = _sut.CanSubscribe(subscriber, pattern);

        result.Allowed.Should().BeFalse();
        result.Reason.Should().Contain("no permission mapping");
    }
}
