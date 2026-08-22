using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class NotificationServiceTests : IDisposable
{
    // ── shared mocks ─────────────────────────────────────────────────────────

    private readonly INotificationStore _notificationStore = Substitute.For<INotificationStore>();
    private readonly IUserRoleStore _userRoleStore = Substitute.For<IUserRoleStore>();
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly ITenantBrandingStore _brandingStore = Substitute.For<ITenantBrandingStore>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailTemplateService _templateService = Substitute.For<IEmailTemplateService>();
    private readonly IOptions<SmtpOptions> _smtpOptions = Options.Create(new SmtpOptions());
    private readonly ILogger<NotificationService> _logger = NullLogger<NotificationService>.Instance;

    private NotificationService CreateService() => new(
        _notificationStore, _userRoleStore, _userStore, _tenantStore,
        _brandingStore, _eventBus, _emailService, _templateService,
        _smtpOptions, _logger);

    public void Dispose() => _eventBus.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetupTenant(string tenantId, string name, string? parentId = null)
    {
        _tenantStore.GetAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = tenantId,
                Name = name,
                Status = TenantStatus.Active,
                ParentTenantId = parentId,
            });
    }

    private void SetupUser(string tenantId, string userId, string email, params string[] roleIds)
    {
        var tid = new TenantId(tenantId);
        var eid = EntityId.From(userId);
        var user = new User
        {
            UserId = eid,
            TenantId = tid,
            Email = email,
            DisplayName = email,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Add user to the store (list returns all users, we accumulate)
        _userStore.ListAsync(tid, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Return any previously configured users + this one
                return new PagedResult<User>([user], 1, 1, 10_000);
            });

        var assignments = roleIds
            .Select(r => new UserRoleAssignment
            {
                TenantId = tid,
                UserId = eid,
                RoleId = r,
                AssignedAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        _userRoleStore.GetRolesForUserAsync(tid, eid, Arg.Any<CancellationToken>())
            .Returns(assignments);
    }

    private void SetupMultipleUsers(string tenantId, params (string userId, string email, string[] roles)[] users)
    {
        var tid = new TenantId(tenantId);
        var userList = new List<User>();

        foreach (var (userId, email, roles) in users)
        {
            var eid = EntityId.From(userId);
            var user = new User
            {
                UserId = eid,
                TenantId = tid,
                Email = email,
                DisplayName = email,
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            userList.Add(user);

            var assignments = roles
                .Select(r => new UserRoleAssignment
                {
                    TenantId = tid,
                    UserId = eid,
                    RoleId = r,
                    AssignedAt = DateTimeOffset.UtcNow,
                })
                .ToList();

            _userRoleStore.GetRolesForUserAsync(tid, eid, Arg.Any<CancellationToken>())
                .Returns(assignments);
        }

        _userStore.ListAsync(tid, Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<User>(userList, userList.Count, 1, 10_000));
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldRouteToAdminUsers_WhenTypeIsBillingDunning()
    {
        // Arrange
        SetupTenant("t1", "Acme");
        SetupUser("t1", "u1", "admin@acme.com", "admin");

        var published = new List<PlatformEvent>();
        using var sub = _eventBus.Events.Subscribe(published.Add);

        var service = CreateService();

        // Act
        await service.CreateAsync("t1", "billing.dunning_escalated",
            "Dunning escalated", "Invoice overdue for 14 days");

        // Assert
        await _notificationStore.Received(1)
            .SaveAsync(Arg.Is<Notification>(n => n != null &&
                n.TenantId == "t1" &&
                n.UserId == "u1" &&
                n.Type == "billing.dunning_escalated" &&
                n.Category == NotificationCategory.Billing &&
                n.Severity == NotificationSeverity.Critical),
                Arg.Any<CancellationToken>());

        published.Should().ContainSingle();
        published.OfType<NotificationEvent>().Should().ContainSingle()
            .Which.NotificationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldDedup_WhenSameTypeWithin5Minutes()
    {
        // Arrange
        SetupTenant("t1", "Acme");
        SetupUser("t1", "u1", "admin@acme.com", "admin");

        var service = CreateService();

        // Act — call twice
        await service.CreateAsync("t1", "billing.quota_warning", "Quota warning", "80% used");
        await service.CreateAsync("t1", "billing.quota_warning", "Quota warning", "80% used");

        // Assert — SaveAsync called only once (second call deduped)
        await _notificationStore.Received(1)
            .SaveAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldSendEmail_WhenSeverityIsCritical()
    {
        // Arrange
        SetupTenant("t1", "Acme");
        SetupUser("t1", "u1", "admin@acme.com", "admin");

        _templateService.Render("notification-critical", Arg.Any<BrandingContext>(),
                Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns("<html>Critical alert</html>");

        var service = CreateService();

        // Act — billing.dunning_escalated is Critical
        await service.CreateAsync("t1", "billing.dunning_escalated",
            "Dunning escalated", "Invoice overdue");

        // Assert
        await _emailService.Received(1)
            .SendAsync(Arg.Is<EmailMessage>(m => m != null &&
                m.Subject == "[Critical] Dunning escalated" &&
                m.Recipients.Count == 1 &&
                m.Recipients[0].Email == "admin@acme.com"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldNotSendEmail_WhenSeverityIsInfo()
    {
        // Arrange
        SetupTenant("t1", "Acme");
        SetupUser("t1", "u1", "admin@acme.com", "platform_admin");

        var service = CreateService();

        // Act — billing.tenant_created is Info severity
        await service.CreateAsync("t1", "billing.tenant_created",
            "Tenant created", "New tenant provisioned");

        // Assert — notification saved but no email
        await _notificationStore.Received(1)
            .SaveAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());

        await _emailService.DidNotReceive()
            .SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldPropagateToPartner_WhenCustomerHasParent()
    {
        // Arrange — customer tenant with parent partner
        SetupTenant("customer-1", "CustomerCo", parentId: "partner-1");
        SetupTenant("partner-1", "PartnerCo");

        SetupUser("customer-1", "u1", "admin@customer.com", "admin");
        SetupMultipleUsers("partner-1",
            ("pu1", "admin@partner.com", ["partner_admin"]));

        _templateService.Render(Arg.Any<string>(), Arg.Any<BrandingContext>(),
                Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns("<html>Alert</html>");

        var service = CreateService();

        // Act — billing.dunning_escalated targets partner roles, should propagate
        await service.CreateAsync("customer-1", "billing.dunning_escalated",
            "Dunning escalated", "Invoice overdue");

        // Assert — notifications in both tenants
        await _notificationStore.Received(1)
            .SaveAsync(Arg.Is<Notification>(n => n != null && n.TenantId == "customer-1"),
                Arg.Any<CancellationToken>());

        await _notificationStore.Received(1)
            .SaveAsync(Arg.Is<Notification>(n => n != null &&
                n.TenantId == "partner-1" &&
                n.Title.Contains("[CustomerCo]")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldSkip_WhenTypeIsUnknown()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.CreateAsync("t1", "unknown.type", "Unknown", "Nothing");

        // Assert — nothing saved, no tenant lookup
        await _notificationStore.DidNotReceive()
            .SaveAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());

        await _tenantStore.DidNotReceive()
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
