using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Automation.Tests;

public class AutomationEngineTests
{
    private readonly IAutomationRuleStore _ruleStore = Substitute.For<IAutomationRuleStore>();
    private readonly IConditionEvaluator _conditionEvaluator = Substitute.For<IConditionEvaluator>();
    private readonly IActionExecutor _actionExecutor = Substitute.For<IActionExecutor>();
    private readonly IAutomationExecutionLogStore _logStore = Substitute.For<IAutomationExecutionLogStore>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private static readonly TenantId Tenant = new("t1");
    private static readonly EntityId ConvId = EntityId.From("conv-001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly AutomationEngine _sut;

    public AutomationEngineTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new AutomationEngine(
            _ruleStore,
            _conditionEvaluator,
            _actionExecutor,
            _logStore,
            _clock,
            NullLogger<AutomationEngine>.Instance);
    }

    private static AutomationEvent BuildEvent(AutomationTrigger trigger = AutomationTrigger.MessageReceived) =>
        new()
        {
            Trigger = trigger,
            TenantId = Tenant,
            ConversationId = ConvId,
            OccurredAt = Now,
        };

    private static AutomationRule BuildRule(
        string ruleId = "rule-001",
        int priority = 100,
        bool isActive = true,
        int maxExecutions = 1,
        IReadOnlyList<AutomationCondition>? conditions = null,
        IReadOnlyList<AutomationAction>? actions = null)
    {
        return new AutomationRule
        {
            RuleId = EntityId.From(ruleId),
            TenantId = Tenant,
            Name = $"Rule {ruleId}",
            Trigger = AutomationTrigger.MessageReceived,
            Conditions = conditions ?? [],
            Actions = actions ?? [new AutomationAction { Type = AutomationActionType.AddTag, Config = new Dictionary<string, string>() }],
            IsActive = isActive,
            Priority = priority,
            MaxExecutionsPerConversation = maxExecutions,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    // --- Basic matching ---

    [Fact]
    public async Task ProcessEventAsync_ShouldExecuteActions_WhenConditionsMatch()
    {
        var rule = BuildRule();
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.Received(1).ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEventAsync_ShouldSkipActions_WhenConditionFalse()
    {
        var condition = new AutomationCondition { Field = "channel", Operator = ConditionOperator.Equals, Value = "Email" };
        var rule = BuildRule(conditions: [condition]);
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);
        _conditionEvaluator.EvaluateAsync(condition, Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(false);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    // --- Priority ordering ---

    [Fact]
    public async Task ProcessEventAsync_ShouldExecuteInPriorityOrder_LowerFirst()
    {
        var executionOrder = new List<string>();

        static AutomationRule RuleWithTag(string ruleId, int priority) => new()
        {
            RuleId = EntityId.From(ruleId),
            TenantId = Tenant,
            Name = $"Rule {ruleId}",
            Trigger = AutomationTrigger.MessageReceived,
            Conditions = [],
            Actions = [new AutomationAction { Type = AutomationActionType.AddTag, Config = new Dictionary<string, string> { ["_ruleId"] = ruleId } }],
            IsActive = true,
            Priority = priority,
            MaxExecutionsPerConversation = 5,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var rules = new List<AutomationRule>
        {
            RuleWithTag("rule-010", priority: 10),
            RuleWithTag("rule-050", priority: 50),
            RuleWithTag("rule-005", priority: 5),
        };

        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs(rules);

        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);

        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var action = callInfo.ArgAt<AutomationAction>(0);
                executionOrder.Add(action.Config.GetValueOrDefault("_ruleId", "?"));
                return Task.FromResult(true);
            });

        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        executionOrder.Should().ContainInOrder("rule-005", "rule-010", "rule-050");
    }

    // --- Max executions per conversation ---

    [Fact]
    public async Task ProcessEventAsync_ShouldSkipRule_WhenMaxExecutionsReached()
    {
        var rule = BuildRule(maxExecutions: 1);
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);
        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.Received(1).ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    // --- Loop protection ---

    [Fact]
    public async Task ProcessEventAsync_ShouldStopAfter10Activations_LoopProtection()
    {
        // 15 rules, each with maxExecutions=999 — only 10 should fire
        var rules = Enumerable.Range(1, 15)
            .Select(i => BuildRule($"rule-{i:D3}", priority: i, maxExecutions: 999))
            .ToList();

        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs(rules);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.Received(10).ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    // --- Execution logging ---

    [Fact]
    public async Task ProcessEventAsync_ShouldLogExecution_WhenRuleEvaluated()
    {
        var rule = BuildRule();
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _logStore.Received(1).SaveAsync(
            Arg.Is<AutomationExecutionLog>(l => l != null && l.ConditionsMatched && l.RuleId == rule.RuleId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessEventAsync_ShouldLogConditionsNotMatched_WhenConditionFails()
    {
        var condition = new AutomationCondition { Field = "channel", Operator = ConditionOperator.Equals, Value = "Email" };
        var rule = BuildRule(conditions: [condition]);
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(false);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _logStore.Received(1).SaveAsync(
            Arg.Is<AutomationExecutionLog>(l => l != null && !l.ConditionsMatched),
            Arg.Any<CancellationToken>());
    }

    // --- Multiple rules can match ---

    [Fact]
    public async Task ProcessEventAsync_ShouldActivateMultipleRules_WhenAllConditionsMatch()
    {
        var rule1 = BuildRule("rule-001", maxExecutions: 5);
        var rule2 = BuildRule("rule-002", maxExecutions: 5);

        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule1, rule2]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.Received(2).ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    // --- Inactive rules skipped ---

    [Fact]
    public async Task ProcessEventAsync_ShouldSkipInactiveRules()
    {
        var activeRule = BuildRule("rule-active", isActive: true);
        var inactiveRule = BuildRule("rule-inactive", isActive: false);

        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([activeRule, inactiveRule]);
        _conditionEvaluator.EvaluateAsync(Arg.Any<AutomationCondition>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _actionExecutor.ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), default)
            .ReturnsForAnyArgs(true);
        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.Received(1).ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }

    // --- No rules ---

    [Fact]
    public async Task ProcessEventAsync_ShouldDoNothing_WhenNoRulesFound()
    {
        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs(new List<AutomationRule>());

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
        await _logStore.DidNotReceive().SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>());
    }

    // --- AllConditions must match ---

    [Fact]
    public async Task ProcessEventAsync_ShouldSkip_WhenNotAllConditionsMatch()
    {
        var cond1 = new AutomationCondition { Field = "channel", Operator = ConditionOperator.Equals, Value = "Email" };
        var cond2 = new AutomationCondition { Field = "conversation.state", Operator = ConditionOperator.Equals, Value = "Active" };
        var rule = BuildRule(conditions: [cond1, cond2]);

        _ruleStore.GetActiveByTriggerAsync(Tenant, AutomationTrigger.MessageReceived, default)
            .ReturnsForAnyArgs([rule]);

        // First condition passes, second fails
        _conditionEvaluator.EvaluateAsync(cond1, Arg.Any<AutomationEvent>(), default).ReturnsForAnyArgs(true);
        _conditionEvaluator.EvaluateAsync(cond2, Arg.Any<AutomationEvent>(), default).ReturnsForAnyArgs(false);

        _logStore.SaveAsync(Arg.Any<AutomationExecutionLog>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ProcessEventAsync(BuildEvent(), default);

        await _actionExecutor.DidNotReceive().ExecuteAsync(Arg.Any<AutomationAction>(), Arg.Any<AutomationEvent>(), Arg.Any<CancellationToken>());
    }
}
