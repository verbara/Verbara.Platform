using Verbara.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Queues.Tests;

public class QueueTests
{
    [Fact]
    public void Constructor_ShouldCreateQueue_WhenValidInput()
    {
        var queue = new Queue
        {
            QueueId = EntityId.From("q-001"),
            TenantId = new TenantId("t1"),
            Name = "Support",
            IsActive = true,
            MaxWaiting = 50,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        queue.Name.Should().Be("Support");
        queue.MaxWaiting.Should().Be(50);
    }

    [Fact]
    public void SlaTargets_ShouldBeConfigurable()
    {
        var queue = new Queue
        {
            QueueId = EntityId.From("q-001"),
            TenantId = new TenantId("t1"),
            Name = "Sales",
            IsActive = true,
            SlaTargets = new SlaPolicyTarget
            {
                AnswerWithinSeconds = 30,
                FirstResponseWithinSeconds = 60,
                ResolutionWithinSeconds = 3600,
            },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        queue.SlaTargets!.AnswerWithinSeconds.Should().Be(30);
    }

    [Fact]
    public void Csat_ShouldBeNull_WhenNotConfigured()
    {
        var queue = new Queue
        {
            QueueId = EntityId.From("q-001"),
            TenantId = new TenantId("t1"),
            Name = "Support",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        queue.Csat.Should().BeNull();
    }

    [Fact]
    public void Csat_ShouldRoundTripConfig_WhenAssigned()
    {
        var queue = new Queue
        {
            QueueId = EntityId.From("q-001"),
            TenantId = new TenantId("t1"),
            Name = "Support",
            CreatedAt = DateTimeOffset.UtcNow,
            Csat = new CsatConfig(
                Enabled: true,
                PreferredChannel: "webchat",
                PromptTemplateId: EntityId.From("tpl-1"),
                SamplingRatePercent: 20),
        };

        queue.Csat.Should().NotBeNull();
        queue.Csat!.Enabled.Should().BeTrue();
        queue.Csat.PreferredChannel.Should().Be("webchat");
        queue.Csat.PromptTemplateId.Should().Be(EntityId.From("tpl-1"));
        queue.Csat.SamplingRatePercent.Should().Be(20);
    }
}
