using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class TenantProvisioningService : ITenantLifecycleHandler
{
    private readonly ITenantRoleStore _roleStore;
    private readonly IRoleTemplateStore _templateStore;
    private readonly IQueueStore _queueStore;
    private readonly ISurveyStore _surveyStore;
    private readonly IAutomationRuleStore _automationStore;
    private readonly IFlowStore _flowStore;
    private readonly ITenantAuthConfigStore _authConfigStore;
    private readonly ITenantRetentionPolicyStore _retentionStore;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        ITenantRoleStore roleStore,
        IRoleTemplateStore templateStore,
        IQueueStore queueStore,
        ISurveyStore surveyStore,
        IAutomationRuleStore automationStore,
        IFlowStore flowStore,
        ITenantAuthConfigStore authConfigStore,
        ITenantRetentionPolicyStore retentionStore,
        ILogger<TenantProvisioningService> logger)
    {
        _roleStore = roleStore;
        _templateStore = templateStore;
        _queueStore = queueStore;
        _surveyStore = surveyStore;
        _automationStore = automationStore;
        _flowStore = flowStore;
        _authConfigStore = authConfigStore;
        _retentionStore = retentionStore;
        _logger = logger;
    }

    public async ValueTask OnTenantCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var tenantId = new TenantId(tenant.TenantId);
        var plan = tenant.GetPlan();

        var planName = plan.ToString();
        LogProvisioningStarted(_logger, tenant.TenantId, planName);

        // ── Golden Defaults (always) ──

        await CloneRoleTemplatesAsync(tenantId, cancellationToken);
        await CreateAuthConfigAsync(tenantId, plan, cancellationToken);
        await CreateRetentionPolicyAsync(tenant.TenantId, plan, cancellationToken);
        await CreateDefaultSurveyAsync(tenantId, cancellationToken);

        // ── Template resources (if template valid) ──

        var template = tenant.Metadata?.GetValueOrDefault("OnboardingTemplate");
        if (!string.IsNullOrEmpty(template) && TenantProvisioningTemplates.ValidTemplateNames.Contains(template))
        {
            await ApplyTemplateResourcesAsync(tenant, template, cancellationToken);
            LogTemplateApplied(_logger, tenant.TenantId, template);
        }

        LogProvisioningCompleted(_logger, tenant.TenantId);
    }

    public ValueTask OnTenantSuspendedAsync(string tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask OnTenantDeletedAsync(string tenantId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Applies only template-specific resources (queues, flows, automation rules).
    /// Used by the onboarding endpoint when a template is applied post-creation.
    /// </summary>
    public async Task ApplyTemplateAsync(Tenant tenant, string template, CancellationToken cancellationToken = default)
    {
        if (!TenantProvisioningTemplates.ValidTemplateNames.Contains(template))
        {
            LogInvalidTemplate(_logger, tenant.TenantId, template);
            return;
        }

        await ApplyTemplateResourcesAsync(tenant, template, cancellationToken);
        LogTemplateApplied(_logger, tenant.TenantId, template);
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private async Task CloneRoleTemplatesAsync(TenantId tenantId, CancellationToken ct)
    {
        var templates = await _templateStore.GetAllAsync(ct);
        foreach (var tmpl in templates)
        {
            var roleId = $"role_{tmpl.TemplateId}_{tenantId}";
            await _roleStore.CloneFromTemplateAsync(tenantId, roleId, tmpl.TemplateId, tmpl.Name, tmpl.Description, ct);
        }

        LogRoleTemplatesCloned(_logger, tenantId.Value, templates.Count);
    }

    private async Task CreateAuthConfigAsync(TenantId tenantId, TenantPlan plan, CancellationToken ct)
    {
        var config = plan switch
        {
            TenantPlan.Enterprise => new TenantAuthConfig
            {
                TenantId = tenantId.Value,
                MfaPolicy = "required_for_roles",
                MfaRequiredRoles = ["Admin", "Supervisor"],
                PasswordMinLength = 16,
                PasswordRequireUppercase = true,
                PasswordRequireNumber = true,
                PasswordRequireSpecial = true,
            },
            TenantPlan.Pro => new TenantAuthConfig
            {
                TenantId = tenantId.Value,
                MfaPolicy = "optional",
                PasswordMinLength = 12,
                PasswordRequireUppercase = true,
                PasswordRequireNumber = true,
                PasswordRequireSpecial = true,
            },
            _ => new TenantAuthConfig
            {
                TenantId = tenantId.Value,
                MfaPolicy = "optional",
                PasswordMinLength = 8,
                PasswordRequireUppercase = true,
                PasswordRequireNumber = true,
                PasswordRequireSpecial = false,
            },
        };

        await _authConfigStore.SaveAsync(config, ct);
    }

    private async Task CreateRetentionPolicyAsync(string tenantId, TenantPlan plan, CancellationToken ct)
    {
        var retentionDays = plan switch
        {
            TenantPlan.Enterprise => 730,
            TenantPlan.Pro => 365,
            _ => 90,
        };

        var policy = new TenantRetentionPolicy
        {
            TenantId = tenantId,
            ConversationRetentionDays = retentionDays,
            AuthEventRetentionDays = retentionDays,
            AuditRetentionDays = retentionDays,
            UsageRecordRetentionDays = retentionDays,
        };

        await _retentionStore.SaveAsync(policy, ct);
    }

    private async Task CreateDefaultSurveyAsync(TenantId tenantId, CancellationToken ct)
    {
        var survey = TenantProvisioningTemplates.CreateDefaultCsatSurvey(tenantId);
        await _surveyStore.SaveAsync(survey, ct);
    }

    private async Task ApplyTemplateResourcesAsync(Tenant tenant, string template, CancellationToken ct)
    {
        var tenantId = new TenantId(tenant.TenantId);
        var timezone = tenant.Metadata?.GetValueOrDefault("Timezone") ?? "UTC";

        var queues = TenantProvisioningTemplates.GetQueues(template, tenantId, timezone);
        foreach (var queue in queues)
            await _queueStore.SaveAsync(queue, ct);

        var flows = TenantProvisioningTemplates.GetFlows(template, tenantId, tenant.Name);
        foreach (var flow in flows)
            await _flowStore.SaveAsync(flow, ct);

        var rules = TenantProvisioningTemplates.GetAutomationRules(template, tenantId);
        foreach (var rule in rules)
            await _automationStore.SaveAsync(rule, ct);
    }

    // ── AOT-safe log messages ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning tenant {TenantId} with plan {Plan}")]
    private static partial void LogProvisioningStarted(ILogger logger, string tenantId, string plan);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provisioning completed for tenant {TenantId}")]
    private static partial void LogProvisioningCompleted(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied template {Template} to tenant {TenantId}")]
    private static partial void LogTemplateApplied(ILogger logger, string tenantId, string template);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid template {Template} requested for tenant {TenantId}")]
    private static partial void LogInvalidTemplate(ILogger logger, string tenantId, string template);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cloned {Count} role templates for tenant {TenantId}")]
    private static partial void LogRoleTemplatesCloned(ILogger logger, string tenantId, int count);
}
