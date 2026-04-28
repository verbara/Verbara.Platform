using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class AccountLockoutService
{
    private readonly IUserStore _userStore;
    private readonly ITenantAuthConfigStore _configStore;
    private readonly AuthEventService _authEvents;
    private readonly AuthWriteQueue? _queue;

    public AccountLockoutService(
        IUserStore userStore,
        ITenantAuthConfigStore configStore,
        AuthEventService authEvents,
        AuthWriteQueue? queue = null)
    {
        _userStore = userStore;
        _configStore = configStore;
        _authEvents = authEvents;
        _queue = queue;
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
                new Dictionary<string, string> { ["threshold"] = config.LockoutThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ct);
        }

        await _userStore.SaveAsync(user, ct);
    }

    /// <summary>
    /// Reset the user's lockout counters on a successful login. AHH Phase 2:
    /// the in-memory <see cref="User"/> snapshot is updated synchronously so
    /// the rest of the request path (JWT issuance, response shaping) sees
    /// the post-reset state, but the persistence is deferred to
    /// <see cref="AuthWriteQueue"/>. When the queue is not registered (tests
    /// / single-process bootstrap) the original synchronous DB save is used.
    /// </summary>
    public async Task ResetAttemptsAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;

        if (_queue is not null)
        {
            _queue.TryEnqueue(new ResetLockoutCountersCommand(user.TenantId.Value, user.UserId.Value));
            return;
        }

        await _userStore.SaveAsync(user, ct);
    }

    /// <summary>
    /// AHH Phase 2 — defer the <c>users.last_login_at</c> upsert to
    /// <see cref="AuthWriteQueue"/>. The in-memory <paramref name="user"/>
    /// snapshot is updated synchronously so the request can ship its
    /// response with the new timestamp; persistence is async. Idempotent
    /// when the queue is not registered (tests fall back to sync save).
    /// </summary>
    public async Task EnqueueLastLoginAtUpdateAsync(User user, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.LastLoginAt = at;

        if (_queue is not null)
        {
            _queue.TryEnqueue(new UpdateLastLoginAtCommand(user.TenantId.Value, user.UserId.Value, at));
            return;
        }

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
