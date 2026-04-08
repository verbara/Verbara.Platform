using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Core;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public class TenantProvisioningServiceTests
{
    private readonly ITenantRoleStore _roleStore = Substitute.For<ITenantRoleStore>();
    private readonly IRoleTemplateStore _templateStore = Substitute.For<IRoleTemplateStore>();
    private readonly IQueueStore _queueStore = Substitute.For<IQueueStore>();
    private readonly ISurveyStore _surveyStore = Substitute.For<ISurveyStore>();
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
            _roleStore, _templateStore, _queueStore, _surveyStore,
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
            Arg.Is<Survey>(s => s.Type == SurveyType.Csat),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnTenantCreated_ShouldCreateRetentionPolicy()
    {
        var tenant = CreateTenant();

        await _sut.OnTenantCreatedAsync(tenant);

        await _retentionStore.Received(1).SaveAsync(
            Arg.Is<TenantRetentionPolicy>(p =>
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
            Arg.Is<Queue>(q => q.Name == "General Support"),
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
