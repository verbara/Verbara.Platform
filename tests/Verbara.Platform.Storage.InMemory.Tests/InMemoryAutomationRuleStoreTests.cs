using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;

namespace Verbara.Platform.Storage.InMemory.Tests;

public sealed class InMemoryAutomationRuleStoreTests
{
    private static readonly TenantId Tenant = new("tenant-1");

    private static AutomationRule MakeRule(AutomationTrigger trigger, bool isActive = true, int priority = 100) =>
        new AutomationRule
        {
            RuleId = EntityId.New(),
            TenantId = Tenant,
            Name = "Test Rule",
            Trigger = trigger,
            IsActive = isActive,
            Priority = priority,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetActiveByTriggerAsync_ShouldReturnOnlyMatchingActiveRules()
    {
        var store = new InMemoryAutomationRuleStore();
        var activeMsg = MakeRule(AutomationTrigger.MessageReceived, isActive: true);
        var inactiveMsg = MakeRule(AutomationTrigger.MessageReceived, isActive: false);
        var activeState = MakeRule(AutomationTrigger.StateChanged, isActive: true);

        await store.SaveAsync(activeMsg, CancellationToken.None);
        await store.SaveAsync(inactiveMsg, CancellationToken.None);
        await store.SaveAsync(activeState, CancellationToken.None);

        var result = await store.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].RuleId.Should().Be(activeMsg.RuleId);
    }

    [Fact]
    public async Task GetActiveByTriggerAsync_ShouldReturnResultsOrderedByPriority()
    {
        var store = new InMemoryAutomationRuleStore();
        var rule1 = MakeRule(AutomationTrigger.ConversationCreated, priority: 200);
        var rule2 = MakeRule(AutomationTrigger.ConversationCreated, priority: 50);
        var rule3 = MakeRule(AutomationTrigger.ConversationCreated, priority: 100);

        await store.SaveAsync(rule1, CancellationToken.None);
        await store.SaveAsync(rule2, CancellationToken.None);
        await store.SaveAsync(rule3, CancellationToken.None);

        var result = await store.GetActiveByTriggerAsync(Tenant, AutomationTrigger.ConversationCreated, CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Priority.Should().Be(50);
        result[1].Priority.Should().Be(100);
        result[2].Priority.Should().Be(200);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnRule()
    {
        var store = new InMemoryAutomationRuleStore();
        var rule = MakeRule(AutomationTrigger.SlaBreached);
        await store.SaveAsync(rule, CancellationToken.None);

        var result = await store.GetByIdAsync(Tenant, rule.RuleId, CancellationToken.None);

        result.Should().BeSameAs(rule);
    }
}
