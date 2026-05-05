using Verbara.Platform.Bot;
using Verbara.Platform.Core;

namespace Verbara.Platform.Bot.Tests;

public class BotConfigurationTests
{
    private readonly TenantId _tenantId = new("t1");
    private readonly EntityId _botId = EntityId.From("bot-001");
    private readonly EntityId _flowId = EntityId.From("flow-001");

    [Fact]
    public void BotConfiguration_ShouldHaveDefaultThreshold()
    {
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
        };

        config.ConfidenceThreshold.Should().Be(0.7);
    }

    [Fact]
    public void BotConfiguration_ShouldHaveDefaultMaxTurns()
    {
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
        };

        config.MaxTurnsBeforeHandoff.Should().Be(20);
    }

    [Fact]
    public void BotConfiguration_ShouldBeActiveByDefault()
    {
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
        };

        config.IsActive.Should().BeTrue();
    }

    [Fact]
    public void BotConfiguration_ShouldAllowCustomThreshold()
    {
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
            ConfidenceThreshold = 0.9,
        };

        config.ConfidenceThreshold.Should().Be(0.9);
    }

    [Fact]
    public void BotConfiguration_ShouldAllowFallbackQueue()
    {
        var queueId = EntityId.From("queue-001");
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
            FallbackQueueId = queueId,
        };

        config.FallbackQueueId.Should().Be(queueId);
    }

    [Fact]
    public void BotConfiguration_ShouldImplementITenantScoped()
    {
        var config = new BotConfiguration
        {
            BotId = _botId,
            TenantId = _tenantId,
            Name = "Test Bot",
            DefaultFlowId = _flowId,
        };

        config.Should().BeAssignableTo<ITenantScoped>();
        ((ITenantScoped)config).TenantId.Should().Be(_tenantId);
    }
}
