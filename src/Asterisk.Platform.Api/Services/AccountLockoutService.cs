using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class AccountLockoutService
{
    private readonly IUserStore _userStore;
    private readonly ITenantAuthConfigStore _configStore;
    private readonly AuthEventService _authEvents;

    public AccountLockoutService(
        IUserStore userStore,
        ITenantAuthConfigStore configStore,
        AuthEventService authEvents)
    {
        _userStore = userStore;
        _configStore = configStore;
        _authEvents = authEvents;
    }

    public async Task RecordFailedAttemptAsync(
        User user,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        user.FailedLoginAttempts++;

        var config = await _configStore.GetAsync(user.TenantId.Value, ct)
            ?? new TenantAuthConfig { TenantId = user.TenantId.Value };

        if (user.FailedLoginAttempts >= config.LockoutThreshold)
        {
            user.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(config.LockoutDurationMinutes);

            await _authEvents.LogAsync(
                user.TenantId.Value,
                user.UserId.Value,
                AuthEventTypes.Lockout,
                ipAddress,
                userAgent,
                new { threshold = config.LockoutThreshold },
                ct);
        }

        await _userStore.SaveAsync(user, ct);
    }

    public async Task ResetAttemptsAsync(User user, CancellationToken ct)
    {
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await _userStore.SaveAsync(user, ct);
    }

    public async Task UnlockAsync(
        TenantId tenantId,
        EntityId userId,
        string? adminIp,
        string? adminUserAgent,
        CancellationToken ct)
    {
        var user = await _userStore.GetByIdAsync(tenantId, userId, ct);
        if (user is null) return;

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await _userStore.SaveAsync(user, ct);

        await _authEvents.LogAsync(
            tenantId.Value,
            userId.Value,
            "admin_unlock",
            adminIp,
            adminUserAgent,
            null,
            ct);
    }
}
