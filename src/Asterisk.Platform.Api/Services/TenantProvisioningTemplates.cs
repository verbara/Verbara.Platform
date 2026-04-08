using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Api.Services;

internal static class TenantProvisioningTemplates
{
    public static readonly IReadOnlySet<string> ValidTemplateNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "support", "sales", "blended" };

    // ── Queues ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<Queue> GetQueues(string template, TenantId tenantId, string timezone)
        => template.ToLowerInvariant() switch
        {
            "support" => [CreateSupportQueue(tenantId, timezone)],
            "sales" => [CreateSalesInboundQueue(tenantId, timezone), CreateSalesOutboundQueue(tenantId, timezone)],
            "blended" => [CreateSupportQueue(tenantId, timezone), CreateSalesInboundQueue(tenantId, timezone), CreateVipQueue(tenantId)],
            _ => [],
        };

    // ── Flows ───────────────────────────────────────────────────────────────

    public static IReadOnlyList<FlowDefinition> GetFlows(string template, TenantId tenantId, string tenantName)
        => template.ToLowerInvariant() switch
        {
            "support" => [CreateSupportWelcomeFlow(tenantId, tenantName)],
            "sales" => [CreateSalesGreetingFlow(tenantId, tenantName)],
            "blended" => [CreateWelcomeRoutingFlow(tenantId, tenantName)],
            _ => [],
        };

    // ── Automation Rules ────────────────────────────────────────────────────

    public static IReadOnlyList<AutomationRule> GetAutomationRules(string template, TenantId tenantId)
        => template.ToLowerInvariant() switch
        {
            "support" => [CreateAutoCloseRule(tenantId), CreateSlaBreachEscalationRule(tenantId)],
            "sales" => [CreateHotLeadPriorityRule(tenantId), CreateFollowUp24hRule(tenantId)],
            "blended" => [CreateAutoCloseRule(tenantId), CreateSlaBreachEscalationRule(tenantId), CreateVipDetectionRule(tenantId)],
            _ => [],
        };

    // ── CSAT Survey ─────────────────────────────────────────────────────────

    public static Survey CreateDefaultCsatSurvey(TenantId tenantId) => new()
    {
        SurveyId = EntityId.New(),
        TenantId = tenantId,
        Name = "Customer Satisfaction",
        Type = SurveyType.Csat,
        IsActive = true,
        Questions =
        [
            new SurveyQuestion
            {
                QuestionId = EntityId.New(),
                Text = "How satisfied are you with the support you received?",
                Type = SurveyQuestionType.Scale,
            },
            new SurveyQuestion
            {
                QuestionId = EntityId.New(),
                Text = "Any additional comments?",
                Type = SurveyQuestionType.FreeText,
            },
        ],
    };

    // ── Private: Queue builders ─────────────────────────────────────────────

    private static Queue CreateSupportQueue(TenantId tenantId, string timezone) => new()
    {
        QueueId = EntityId.New(),
        TenantId = tenantId,
        Name = "General Support",
        IsActive = true,
        SlaTargets = new SlaPolicyTarget
        {
            AnswerWithinSeconds = 20,
            FirstResponseWithinSeconds = 1800,
            ResolutionWithinSeconds = 14400,
        },
        Hours = CreateBusinessHours(timezone),
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 30, ForceWrapUp = false },
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedBy = "system",
    };

    private static Queue CreateSalesInboundQueue(TenantId tenantId, string timezone) => new()
    {
        QueueId = EntityId.New(),
        TenantId = tenantId,
        Name = "Sales Inbound",
        IsActive = true,
        SlaTargets = new SlaPolicyTarget
        {
            AnswerWithinSeconds = 15,
            FirstResponseWithinSeconds = 600,
            ResolutionWithinSeconds = 3600,
        },
        Hours = CreateBusinessHours(timezone),
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 30, ForceWrapUp = false },
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedBy = "system",
    };

    private static Queue CreateSalesOutboundQueue(TenantId tenantId, string timezone) => new()
    {
        QueueId = EntityId.New(),
        TenantId = tenantId,
        Name = "Sales Outbound",
        IsActive = true,
        SlaTargets = new SlaPolicyTarget
        {
            AnswerWithinSeconds = 15,
            FirstResponseWithinSeconds = 600,
            ResolutionWithinSeconds = 3600,
        },
        Hours = CreateBusinessHours(timezone),
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 30, ForceWrapUp = false },
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedBy = "system",
    };

    private static Queue CreateVipQueue(TenantId tenantId) => new()
    {
        QueueId = EntityId.New(),
        TenantId = tenantId,
        Name = "VIP",
        IsActive = true,
        SlaTargets = new SlaPolicyTarget
        {
            AnswerWithinSeconds = 10,
            FirstResponseWithinSeconds = 300,
            ResolutionWithinSeconds = 1800,
        },
        Hours = HoursOfOperation.AlwaysOpen(),
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = 30, ForceWrapUp = false },
        CreatedAt = DateTimeOffset.UtcNow,
        CreatedBy = "system",
    };

    private static HoursOfOperation CreateBusinessHours(string timezone)
    {
        var hours = new HoursOfOperation(timezone);
        var open = new TimeOnly(9, 0);
        var close = new TimeOnly(18, 0);

        hours.SetDaySchedule(DayOfWeek.Monday, open, close);
        hours.SetDaySchedule(DayOfWeek.Tuesday, open, close);
        hours.SetDaySchedule(DayOfWeek.Wednesday, open, close);
        hours.SetDaySchedule(DayOfWeek.Thursday, open, close);
        hours.SetDaySchedule(DayOfWeek.Friday, open, close);

        return hours;
    }

    // ── Private: Flow builders ──────────────────────────────────────────────

    private static FlowDefinition CreateSupportWelcomeFlow(TenantId tenantId, string tenantName)
    {
        var greetNodeId = EntityId.New();
        var menuNodeId = EntityId.New();
        var endNodeId = EntityId.New();

        return new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = tenantId,
            Name = "Support Welcome",
            Version = 1,
            IsPublished = true,
            EntryNodeId = greetNodeId,
            CreatedAt = DateTimeOffset.UtcNow,
            Nodes =
            [
                new FlowNode
                {
                    NodeId = greetNodeId,
                    Type = "message",
                    Config = new Dictionary<string, string>
                    {
                        ["text"] = $"Welcome to {tenantName} Support. How can we help you today?",
                    },
                    Edges = [new FlowEdge("default", menuNodeId)],
                },
                new FlowNode
                {
                    NodeId = menuNodeId,
                    Type = "menu",
                    Config = new Dictionary<string, string>
                    {
                        ["prompt"] = "Please select an option:",
                        ["option_1"] = "Technical Support",
                        ["option_2"] = "Billing Inquiry",
                        ["option_3"] = "General Question",
                    },
                    Edges = [new FlowEdge("default", endNodeId)],
                },
                new FlowNode
                {
                    NodeId = endNodeId,
                    Type = "transfer",
                    Config = new Dictionary<string, string>
                    {
                        ["target"] = "queue",
                    },
                    Edges = [],
                },
            ],
        };
    }

    private static FlowDefinition CreateSalesGreetingFlow(TenantId tenantId, string tenantName)
    {
        var greetNodeId = EntityId.New();
        var qualifyNodeId = EntityId.New();
        var endNodeId = EntityId.New();

        return new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = tenantId,
            Name = "Sales Greeting",
            Version = 1,
            IsPublished = true,
            EntryNodeId = greetNodeId,
            CreatedAt = DateTimeOffset.UtcNow,
            Nodes =
            [
                new FlowNode
                {
                    NodeId = greetNodeId,
                    Type = "message",
                    Config = new Dictionary<string, string>
                    {
                        ["text"] = $"Welcome to {tenantName}! A sales representative will be with you shortly.",
                    },
                    Edges = [new FlowEdge("default", qualifyNodeId)],
                },
                new FlowNode
                {
                    NodeId = qualifyNodeId,
                    Type = "collect",
                    Config = new Dictionary<string, string>
                    {
                        ["field"] = "company_name",
                        ["prompt"] = "May I have your company name?",
                    },
                    Edges = [new FlowEdge("default", endNodeId)],
                },
                new FlowNode
                {
                    NodeId = endNodeId,
                    Type = "transfer",
                    Config = new Dictionary<string, string>
                    {
                        ["target"] = "queue",
                    },
                    Edges = [],
                },
            ],
        };
    }

    private static FlowDefinition CreateWelcomeRoutingFlow(TenantId tenantId, string tenantName)
    {
        var greetNodeId = EntityId.New();
        var routeNodeId = EntityId.New();
        var endNodeId = EntityId.New();

        return new FlowDefinition
        {
            FlowId = EntityId.New(),
            TenantId = tenantId,
            Name = "Welcome Routing",
            Version = 1,
            IsPublished = true,
            EntryNodeId = greetNodeId,
            CreatedAt = DateTimeOffset.UtcNow,
            Nodes =
            [
                new FlowNode
                {
                    NodeId = greetNodeId,
                    Type = "message",
                    Config = new Dictionary<string, string>
                    {
                        ["text"] = $"Welcome to {tenantName}. How can we assist you?",
                    },
                    Edges = [new FlowEdge("default", routeNodeId)],
                },
                new FlowNode
                {
                    NodeId = routeNodeId,
                    Type = "menu",
                    Config = new Dictionary<string, string>
                    {
                        ["prompt"] = "Please select a department:",
                        ["option_1"] = "Support",
                        ["option_2"] = "Sales",
                        ["option_3"] = "VIP",
                    },
                    Edges = [new FlowEdge("default", endNodeId)],
                },
                new FlowNode
                {
                    NodeId = endNodeId,
                    Type = "transfer",
                    Config = new Dictionary<string, string>
                    {
                        ["target"] = "queue",
                    },
                    Edges = [],
                },
            ],
        };
    }

    // ── Private: Automation Rule builders ────────────────────────────────────

    private static AutomationRule CreateAutoCloseRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.New(),
        TenantId = tenantId,
        Name = "Auto-close inactive conversations (30 days)",
        Trigger = AutomationTrigger.TimerElapsed,
        Conditions =
        [
            new AutomationCondition
            {
                Field = "idle_days",
                Operator = ConditionOperator.GreaterThan,
                Value = "30",
            },
        ],
        Actions =
        [
            new AutomationAction
            {
                Type = AutomationActionType.CloseConversation,
                Config = new Dictionary<string, string>
                {
                    ["reason"] = "auto_closed_inactive",
                },
            },
        ],
        IsActive = true,
        Priority = 100,
        MaxExecutionsPerConversation = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateSlaBreachEscalationRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.New(),
        TenantId = tenantId,
        Name = "Escalate on SLA breach",
        Trigger = AutomationTrigger.SlaBreached,
        Conditions = [],
        Actions =
        [
            new AutomationAction
            {
                Type = AutomationActionType.EscalateToSupervisor,
                Config = new Dictionary<string, string>
                {
                    ["reason"] = "sla_breach",
                },
            },
            new AutomationAction
            {
                Type = AutomationActionType.SetPriority,
                Config = new Dictionary<string, string>
                {
                    ["priority"] = "high",
                },
            },
        ],
        IsActive = true,
        Priority = 50,
        MaxExecutionsPerConversation = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateHotLeadPriorityRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.New(),
        TenantId = tenantId,
        Name = "Hot lead priority boost",
        Trigger = AutomationTrigger.MessageReceived,
        Conditions =
        [
            new AutomationCondition
            {
                Field = "message.text",
                Operator = ConditionOperator.Contains,
                Value = "pricing",
            },
        ],
        Actions =
        [
            new AutomationAction
            {
                Type = AutomationActionType.SetPriority,
                Config = new Dictionary<string, string>
                {
                    ["priority"] = "high",
                },
            },
            new AutomationAction
            {
                Type = AutomationActionType.AddTag,
                Config = new Dictionary<string, string>
                {
                    ["tag"] = "hot_lead",
                },
            },
        ],
        IsActive = true,
        Priority = 50,
        MaxExecutionsPerConversation = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateFollowUp24hRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.New(),
        TenantId = tenantId,
        Name = "Follow-up after 24h inactivity",
        Trigger = AutomationTrigger.TimerElapsed,
        Conditions =
        [
            new AutomationCondition
            {
                Field = "idle_hours",
                Operator = ConditionOperator.GreaterThan,
                Value = "24",
            },
        ],
        Actions =
        [
            new AutomationAction
            {
                Type = AutomationActionType.SendMessage,
                Config = new Dictionary<string, string>
                {
                    ["text"] = "Hi! Just checking in — is there anything else we can help you with?",
                },
            },
        ],
        IsActive = true,
        Priority = 80,
        MaxExecutionsPerConversation = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static AutomationRule CreateVipDetectionRule(TenantId tenantId) => new()
    {
        RuleId = EntityId.New(),
        TenantId = tenantId,
        Name = "VIP customer detection",
        Trigger = AutomationTrigger.ConversationCreated,
        Conditions =
        [
            new AutomationCondition
            {
                Field = "contact.tags",
                Operator = ConditionOperator.Contains,
                Value = "vip",
            },
        ],
        Actions =
        [
            new AutomationAction
            {
                Type = AutomationActionType.SetPriority,
                Config = new Dictionary<string, string>
                {
                    ["priority"] = "urgent",
                },
            },
            new AutomationAction
            {
                Type = AutomationActionType.AddTag,
                Config = new Dictionary<string, string>
                {
                    ["tag"] = "vip",
                },
            },
        ],
        IsActive = true,
        Priority = 10,
        MaxExecutionsPerConversation = 1,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
