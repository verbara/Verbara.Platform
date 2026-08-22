using Verbara.Platform.Api.Services;
using Verbara.Platform.Automation;
using Verbara.Platform.Core;
using Verbara.Platform.Flows;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public class TenantProvisioningServiceTests
{
    private readonly ITenantRoleStore _roleStore = Substitute.For<ITenantRoleStore>();
    private readonly IRoleTemplateStore _templateStore = Substitute.For<IRoleTemplateStore>();
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
    private readonly ICsatTemplateStore _csatTemplateStore = Substitute.For<ICsatTemplateStore>();
    private readonly IAutomationRuleStore _automationStore = Substitute.For<IAutomationRuleStore>();
    private readonly IFlowStore _flowStore = Substitute.For<IFlowStore>();
    private readonly ITenantAuthConfigStore _authConfigStore = Substitute.For<ITenantAuthConfigStore>();
    private readonly ITenantRetentionPolicyStore _retentionStore = Substitute.For<ITenantRetentionPolicyStore>();
    private readonly TenantProvisioningService _sut;

    public TenantProvisioningServiceTests()
    {
        _templateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new RoleTemplate { TemplateId = "agent", Name = "Agent", Description = "Default agent", IsSystem = true, CreatedAt = DateTimeOffset.UtcNow },
            new RoleTemplate { TemplateId = "admin", Name = "Admin", Description = "Default admin", IsSystem = true, CreatedAt = DateTimeOffset.UtcNow },
        ]);

        _sut = new TenantProvisioningService(
            _roleStore, _templateStore, _queueStore, _surveyStore, _csatTemplateStore,
            _automationStore, _flowStore, _authConfigStore, _retentionStore,
            NullLogger<TenantProvisioningService>.Instance);
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCloneRoleTemplates()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _roleStore.Received(2).CloneFromTemplateAsync(
            Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCreateCsatSurvey()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _surveyStore.Received(1).SaveAsync(
            Arg.Is<Survey>(s => s != null && s.Type == SurveyType.Csat),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldSeedDefaultCsatTemplates_ForEachLocaleAndChannel()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        // 3 locales (en-US / es-419 / pt-BR) × 3 templatable channels (email / sms / voice).
        await _csatTemplateStore.Received(9).SaveAsync(
            Arg.Is<CsatTemplateEntry>(t => t != null && t.IsDefault && t.TenantId == tenant.TenantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldSeedEnUsVoiceDefaultCsatTemplate()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _csatTemplateStore.Received(1).SaveAsync(
            Arg.Is<CsatTemplateEntry>(t => t != null &&
                t.Channel == "voice" && t.Locale == "en-US" && t.IsDefault && t.Subject == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCreateRetentionPolicy()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _retentionStore.Received(1).SaveAsync(
            Arg.Is<TenantRetentionPolicy>(p => p != null &&
                p.TenantId == tenant.TenantId &&
                p.ConversationRetentionDays == 365),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithSupportTemplate_ShouldCreateQueue()
    {
        var tenant = CreateTenant(template: "support");

        await _sut.OnTenantCreatedAsync(tenant);

        await _queueStore.Received(1).SaveAsync(
            Arg.Is<Queue>(q => q != null && q.Name == "General Support"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithSalesTemplate_ShouldCreateTwoQueues()
    {
        var tenant = CreateTenant(template: "sales");

        await _sut.OnTenantCreatedAsync(tenant);

        await _queueStore.Received(2).SaveAsync(
            Arg.Any<Queue>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_WithNoTemplate_ShouldNotCreateQueues()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _queueStore.DidNotReceive().SaveAsync(
            Arg.Any<Queue>(),
            Arg.Any<CancellationToken>());
    }

    private static Tenant CreateTenant(string? template = null)
    {
        var metadata = new Dictionary<string, string> { ["Plan"] = "Pro" };
        if (template is not null)
            metadata["OnboardingTemplate"] = template;

        return new Tenant
        {
            TenantId = "test-tenant-001",
            Name = "Test Company",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}
