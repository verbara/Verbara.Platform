using Verbara.Platform.Core;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Identity.OidcTokenExchange;

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
            // Only the profile fields the IdP vouches for, only when they differ, and only on the
            // existing row: this sign-in neither recreates a user deleted since the lookup nor writes
            // back anything else it read.
            var change = new UserProfileChange
            {
                DisplayName = claims.Name is not null && !string.Equals(user.DisplayName, claims.Name, StringComparison.Ordinal)
                    ? claims.Name
                    : null,
                EmailVerified = claims.EmailVerified && !user.EmailVerified ? true : null,
            };

            var now = DateTimeOffset.UtcNow;
            if (change.DisplayName is not null || change.EmailVerified is not null)
            {
                if (!await _userStore.UpdateProfileAsync(tid, user.UserId, change, now, ct))
                    return null; // deleted since the lookup
                user.DisplayName = change.DisplayName ?? user.DisplayName;
                user.EmailVerified = change.EmailVerified ?? user.EmailVerified;
                user.UpdatedAt = now;
            }

            if (!await _userStore.SetLastLoginAtAsync(tid, user.UserId, now, ct))
                return null;
            user.LastLoginAt = now;

            LogOidcUserMatched(_logger, claims.Subject, user.UserId.Value, tenantId);

            return user;
        }

        // 2. Fallback: look up by email (for users created before OIDC linking)
        user = await _userStore.GetByEmailAsync(tid, claims.Email, ct);

        if (user is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var link = new UserProfileChange
            {
                OidcSubject = claims.Subject,
                AuthProvider = "oidc",
                EmailVerified = claims.EmailVerified ? true : null,
            };
            if (!await _userStore.UpdateProfileAsync(tid, user.UserId, link, now, ct)
                || !await _userStore.SetLastLoginAtAsync(tid, user.UserId, now, ct))
                return null; // deleted since the lookup

            user.OidcSubject = claims.Subject;
            user.AuthProvider = "oidc";
            user.EmailVerified = user.EmailVerified || claims.EmailVerified;
            user.LastLoginAt = now;
            user.UpdatedAt = now;

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

        await _userStore.CreateAsync(newUser, ct);

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
