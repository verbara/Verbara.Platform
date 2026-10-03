using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

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

    /// <summary>
    /// Records one failed sign-in for <paramref name="user"/>. The store adds it to the stored count
    /// atomically and sets the lock when the count reaches the tenant's threshold, so failures that
    /// read separate copies of the account — parallel guesses — never lose a count. The
    /// <paramref name="user"/> snapshot is updated from what the store returned.
    /// </summary>
    public async Task RecordFailedAttemptAsync(
        User user,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var config = await _configStore.GetAsync(user.TenantId.Value, ct)
            ?? new TenantAuthConfig { TenantId = user.TenantId.Value };

        var recorded = await _userStore.RecordFailedSignInAsync(
            user.TenantId, user.UserId, config.LockoutThreshold,
            DateTimeOffset.UtcNow.AddMinutes(config.LockoutDurationMinutes), ct);
        if (recorded is not { } stored)
            return; // the account no longer exists: there is nothing to count against

        user.FailedLoginAttempts = stored.FailedAttempts;
        user.LockedUntil = stored.LockedUntil;

        if (stored.FailedAttempts >= config.LockoutThreshold)
        {
            await _authEvents.LogAsync(
                user.TenantId.Value,
                user.UserId.Value,
                AuthEventTypes.Lockout,
                ipAddress,
                userAgent,
                new Dictionary<string, string> { ["threshold"] = config.LockoutThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ct);
        }
    }

    /// <summary>
    /// Reset the user's lockout counters on a successful login. AHH Phase 2:
    /// the in-memory <see cref="User"/> snapshot is updated synchronously so
    /// the rest of the request path (JWT issuance, response shaping) sees
    /// the post-reset state, but the persistence is deferred to
    /// <see cref="AuthWriteQueue"/>. When the queue is not registered (tests
    /// / single-process bootstrap) the store is written directly. Either way the
    /// reset lifts no lock that is in force when it is written: a lock set after
    /// this sign-in succeeded stays.
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

        await _userStore.ResetLockoutAsync(user.TenantId, user.UserId, DateTimeOffset.UtcNow, onlyIfUnlocked: true, ct);
    }

    /// <summary>
    /// AHH Phase 2 — defer the <c>users.last_login_at</c> write to
    /// <see cref="AuthWriteQueue"/>. The in-memory <paramref name="user"/>
    /// snapshot is updated synchronously so the request can ship its
    /// response with the new timestamp; persistence is async. When the queue
    /// is not registered (tests) the store is written directly.
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

        await _userStore.SetLastLoginAtAsync(user.TenantId, user.UserId, at, ct);
    }

    public async Task UnlockAsync(
        TenantId tenantId,
        EntityId userId,
        string? adminIp,
        string? adminUserAgent,
        CancellationToken ct)
    {
        if (!await _userStore.ResetLockoutAsync(tenantId, userId, DateTimeOffset.UtcNow, onlyIfUnlocked: false, ct))
            return;

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
