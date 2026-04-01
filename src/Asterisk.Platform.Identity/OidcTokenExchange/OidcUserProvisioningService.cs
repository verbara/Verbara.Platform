using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Identity.OidcTokenExchange;

public sealed partial class OidcUserProvisioningService : IOidcUserProvisioningService
{
    private readonly IUserStore _userStore;
    private readonly ILogger<OidcUserProvisioningService> _logger;

    public OidcUserProvisioningService(
        IUserStore userStore,
        ILogger<OidcUserProvisioningService> logger)
    {
        _userStore = userStore;
        _logger = logger;
    }

    public async Task<User?> ProvisionOrUpdateAsync(
        string tenantId, OidcClaimsResult claims,
        TenantAuthConfig config, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);

        // 1. Look up by OIDC subject (primary identifier from IdP)
        var user = await _userStore.FindByOidcSubjectAsync(tid, claims.Subject, ct);

        if (user is not null)
        {
            var needsUpdate = false;

            if (claims.Name is not null && !string.Equals(user.DisplayName, claims.Name, StringComparison.Ordinal))
            {
                user.DisplayName = claims.Name;
                needsUpdate = true;
            }

            if (claims.EmailVerified && user.EmailVerified != claims.EmailVerified)
            {
                user.EmailVerified = true;
                needsUpdate = true;
            }

            if (needsUpdate)
                user.UpdatedAt = DateTimeOffset.UtcNow;

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await _userStore.SaveAsync(user, ct);

            LogOidcUserMatched(_logger, claims.Subject, user.UserId.Value, tenantId);

            return user;
        }

        // 2. Fallback: look up by email (for users created before OIDC linking)
        user = await _userStore.GetByEmailAsync(tid, claims.Email, ct);

        if (user is not null)
        {
            user.OidcSubject = claims.Subject;
            user.AuthProvider = "oidc";
            user.EmailVerified = user.EmailVerified || claims.EmailVerified;
            user.LastLoginAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await _userStore.SaveAsync(user, ct);

            LogOidcUserLinked(_logger, claims.Subject, user.UserId.Value, tenantId);

            return user;
        }

        // 3. Auto-create new user if enabled
        if (!config.OidcAutoCreateUsers)
        {
            LogOidcAutoCreateDisabled(_logger, claims.Subject, claims.Email, tenantId);
            return null;
        }

        var role = Enum.TryParse<UserRole>(config.OidcDefaultRole, ignoreCase: true, out var parsed)
            ? parsed
            : UserRole.Agent;

        var newUser = new User
        {
            UserId = EntityId.New(),
            TenantId = tid,
            Email = claims.Email,
            DisplayName = claims.Name ?? claims.Email,
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            AuthProvider = "oidc",
            OidcSubject = claims.Subject,
            EmailVerified = claims.EmailVerified,
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        await _userStore.SaveAsync(newUser, ct);

        LogOidcUserCreated(_logger, newUser.UserId.Value, claims.Email, role, tenantId);

        return newUser;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OIDC user {Subject} matched existing user {UserId} in tenant {TenantId}")]
    private static partial void LogOidcUserMatched(ILogger logger, string subject, string userId, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OIDC user {Subject} linked to existing user {UserId} by email in tenant {TenantId}")]
    private static partial void LogOidcUserLinked(ILogger logger, string subject, string userId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OIDC user {Subject} ({Email}) not found and auto-create is disabled for tenant {TenantId}")]
    private static partial void LogOidcAutoCreateDisabled(ILogger logger, string subject, string email, string tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "OIDC auto-created user {UserId} ({Email}) with role {Role} in tenant {TenantId}")]
    private static partial void LogOidcUserCreated(ILogger logger, string userId, string email, UserRole role, string tenantId);
}
