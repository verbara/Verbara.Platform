using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Identity;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Orchestrates notification creation: resolves target users by role, persists notifications,
/// publishes events, sends critical emails, and propagates to parent tenants.
/// </summary>
internal sealed partial class NotificationService : INotificationService
{
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(5);

    private static readonly string[] PartnerRoles =
        ["platform_admin", "partner_admin", "partner_billing", "partner_viewer"];

    private readonly ConcurrentDictionary<string, DateTimeOffset> _dedupCache = new();

    private readonly INotificationStore _notificationStore;
    private readonly IUserRoleStore _userRoleStore;
    private readonly IUserStore _userStore;
    private readonly ITenantStore _tenantStore;
    private readonly ITenantBrandingStore _brandingStore;
    private readonly PlatformEventBus _eventBus;
    private readonly IEmailService _emailService;
    private readonly IEmailTemplateService _templateService;
    private readonly SmtpOptions _smtpOptions;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationStore notificationStore,
        IUserRoleStore userRoleStore,
        IUserStore userStore,
        ITenantStore tenantStore,
        ITenantBrandingStore brandingStore,
        PlatformEventBus eventBus,
        IEmailService emailService,
        IEmailTemplateService templateService,
        IOptions<SmtpOptions> smtpOptions,
        ILogger<NotificationService> logger)
    {
        _notificationStore = notificationStore;
        _userRoleStore = userRoleStore;
        _userStore = userStore;
        _tenantStore = tenantStore;
        _brandingStore = brandingStore;
        _eventBus = eventBus;
        _emailService = emailService;
        _templateService = templateService;
        _smtpOptions = smtpOptions.Value;
        _logger = logger;
    }

    public async Task CreateAsync(
        string tenantId,
        string type,
        string title,
        string body,
        string? actionUrl = null,
        CancellationToken ct = default)
    {
        // 1. Lookup type in registry
        var typeInfo = NotificationTypeRegistry.Get(type);
        if (typeInfo is null)
        {
            LogUnknownType(_logger, type);
            return;
        }

        // 2. Dedup: same (tenantId:type) within 5 minutes
        var dedupKey = $"{tenantId}:{type}";
        var now = DateTimeOffset.UtcNow;

        if (_dedupCache.TryGetValue(dedupKey, out var lastSent) && now - lastSent < DedupWindow)
        {
            LogDedup(_logger, type, tenantId);
            return;
        }

        _dedupCache[dedupKey] = now;

        // 3. Load tenant
        var tenant = await _tenantStore.GetAsync(tenantId, ct);
        if (tenant is null)
        {
            LogTenantNotFound(_logger, tenantId);
            return;
        }

        // 4. Find users with matching roles
        var tenantIdValue = new TenantId(tenantId);
        var allUsers = await _userStore.ListAsync(tenantIdValue, new PagedQuery(1, 10_000), ct);
        var matchingUsers = new List<User>();

        foreach (var user in allUsers.Items)
        {
            var roles = await _userRoleStore.GetRolesForUserAsync(tenantIdValue, user.UserId, ct);
            var hasTargetRole = roles.Any(r =>
                typeInfo.TargetRoles.Any(tr =>
                    string.Equals(r.RoleId, tr, StringComparison.OrdinalIgnoreCase)));

            if (hasTargetRole)
                matchingUsers.Add(user);
        }

        // 5. Create and save notifications for each matching user
        var notifications = new List<Notification>();

        foreach (var user in matchingUsers)
        {
            var notification = new Notification
            {
                NotificationId = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                UserId = user.UserId.Value,
                Category = typeInfo.Category,
                Severity = typeInfo.Severity,
                Type = type,
                Title = title,
                Body = body,
                ActionUrl = actionUrl,
                IsRead = false,
                CreatedAt = now,
            };

            await _notificationStore.SaveAsync(notification, ct);
            notifications.Add(notification);

            _eventBus.Publish(new NotificationEvent(
                tenantId, type, now,
                notification.NotificationId, user.UserId.Value,
                typeInfo.Category, typeInfo.Severity,
                title, body, actionUrl));
        }

        LogNotificationsCreated(_logger, type, tenantId, notifications.Count);

        // 6. Send critical emails
        if (typeInfo.Severity == NotificationSeverity.Critical)
        {
            await SendCriticalEmailsAsync(tenantId, tenant, title, body, actionUrl, matchingUsers, ct);
        }

        // 7. Partner cross-tenant propagation
        if (tenant.ParentTenantId is not null && HasPartnerTargetRole(typeInfo))
        {
            var parentTitle = $"[{tenant.Name}] {title}";
            await CreateAsync(tenant.ParentTenantId, type, parentTitle, body, actionUrl, ct);
        }
    }

    private async Task SendCriticalEmailsAsync(
        string tenantId,
        Tenant tenant,
        string title,
        string body,
        string? actionUrl,
        List<User> users,
        CancellationToken ct)
    {
        try
        {
            var branding = await BuildBrandingContextAsync(tenantId, tenant, ct);

            var variables = new Dictionary<string, string>
            {
                ["title"] = title,
                ["body"] = body,
                ["actionUrl"] = actionUrl ?? string.Empty,
            };

            var htmlBody = _templateService.Render("notification-critical", branding, variables);

            var recipients = users
                .Where(u => !string.IsNullOrWhiteSpace(u.Email))
                .Select(u => new EmailRecipient(u.Email, u.DisplayName))
                .ToList();

            if (recipients.Count == 0)
                return;

            var message = new EmailMessage
            {
                Recipients = recipients,
                Subject = $"[Critical] {title}",
                HtmlBody = htmlBody,
                FromName = branding.FromName,
                FromAddress = branding.FromAddress,
            };

            await _emailService.SendAsync(message, ct);
            LogEmailSent(_logger, tenantId, recipients.Count);
        }
        catch (Exception ex)
        {
            LogEmailError(_logger, tenantId, ex);
        }
    }

    private async Task<BrandingContext> BuildBrandingContextAsync(
        string tenantId, Tenant tenant, CancellationToken ct)
    {
        var branding = await _brandingStore.GetAsync(tenantId, ct);
        TenantBranding? parentBranding = null;

        if (branding is null && tenant.ParentTenantId is not null)
        {
            parentBranding = await _brandingStore.GetAsync(tenant.ParentTenantId, ct);
        }

        var effective = branding ?? parentBranding;

        return new BrandingContext(
            CompanyName: effective?.DisplayName ?? tenant.Name,
            LogoUrl: effective?.LogoUrl,
            PrimaryColor: effective?.PrimaryColor ?? "#1E40AF",
            SecondaryColor: effective?.SecondaryColor ?? "#64748B",
            AccentColor: effective?.AccentColor ?? "#0D9488",
            SupportEmail: effective?.SupportEmail,
            SupportUrl: effective?.SupportUrl,
            FromName: branding?.EmailFromName ?? parentBranding?.EmailFromName ?? _smtpOptions.FromName,
            FromAddress: branding?.EmailFromAddress ?? parentBranding?.EmailFromAddress ?? _smtpOptions.FromAddress);
    }

    private static bool HasPartnerTargetRole(NotificationTypeInfo typeInfo) =>
        typeInfo.TargetRoles.Any(r =>
            PartnerRoles.Any(pr => string.Equals(r, pr, StringComparison.OrdinalIgnoreCase)));

    // ─── AOT-safe LoggerMessage methods ──────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown notification type '{Type}' — skipping")]
    private static partial void LogUnknownType(ILogger logger, string type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dedup: notification type '{Type}' for tenant '{TenantId}' suppressed")]
    private static partial void LogDedup(ILogger logger, string type, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant '{TenantId}' not found — skipping notification")]
    private static partial void LogTenantNotFound(ILogger logger, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Count} notifications for type '{Type}' in tenant '{TenantId}'")]
    private static partial void LogNotificationsCreated(ILogger logger, string type, string tenantId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent critical notification email to {Count} recipients in tenant '{TenantId}'")]
    private static partial void LogEmailSent(ILogger logger, string tenantId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send critical notification email for tenant '{TenantId}'")]
    private static partial void LogEmailError(ILogger logger, string tenantId, Exception ex);
}
