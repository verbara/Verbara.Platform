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
            await LogLockoutAsync(user, config.LockoutThreshold, ipAddress, userAgent, ct);
    }

    /// <summary>
    /// Records one failed sign-in for <paramref name="user"/> <em>before</em> the factor is checked, and
    /// says whether the factor may be checked at all. Checking the lock first and recording the failure
    /// after leaves a window: racing guesses all read the account before any of them is counted, so all
    /// of them pass the lock check. Counting first closes it — the store hands out each count once, so of
    /// any number of racing attempts only those whose count is within the threshold are evaluated (or,
    /// once a lock has expired with the count already at or past it, only the first one, as a wrong
    /// password after expiry gets one try). <paramref name="user"/> must have been read just before.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the account no longer exists. A proven factor hands the reservation to
    /// <see cref="ReleaseReservedAttemptAsync"/>; a rejected one hands it to
    /// <see cref="CompleteReservedFailureAsync"/>, never to <see cref="RecordFailedAttemptAsync"/>, which
    /// would count it twice.
    /// </returns>
    public async Task<FailedAttemptReservation?> ReserveFailedAttemptAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        var config = await _configStore.GetAsync(user.TenantId.Value, ct)
            ?? new TenantAuthConfig { TenantId = user.TenantId.Value };

        var countBefore = user.FailedLoginAttempts;
        var recorded = await _userStore.RecordFailedSignInAsync(
            user.TenantId, user.UserId, config.LockoutThreshold,
            DateTimeOffset.UtcNow.AddMinutes(config.LockoutDurationMinutes), ct);
        if (recorded is not { } stored)
            return null;

        user.FailedLoginAttempts = stored.FailedAttempts;
        user.LockedUntil = stored.LockedUntil;

        var mayEvaluate = stored.FailedAttempts <= config.LockoutThreshold
            || stored.FailedAttempts == countBefore + 1;
        return new FailedAttemptReservation(
            mayEvaluate, ReachedThreshold: stored.FailedAttempts >= config.LockoutThreshold, config.LockoutThreshold);
    }

    /// <summary>
    /// Completes a reservation whose factor was rejected: the failure is already counted, so only the
    /// lockout event is left, emitted as <see cref="RecordFailedAttemptAsync"/> emits it.
    /// </summary>
    public Task CompleteReservedFailureAsync(
        User user, FailedAttemptReservation reservation, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return reservation.ReachedThreshold
            ? LogLockoutAsync(user, reservation.Threshold, ipAddress, userAgent, ct)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Takes back a reservation whose factor was proven. When the reservation itself reached the
    /// threshold it locked the account, and <see cref="ResetAttemptsAsync"/> lifts no lock in force, so the
    /// lock is lifted here, at once: the account holder has just proven both factors. Below the threshold
    /// the ordinary reset clears the count.
    /// </summary>
    public async Task ReleaseReservedAttemptAsync(User user, FailedAttemptReservation reservation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!reservation.ReachedThreshold)
        {
            await ResetAttemptsAsync(user, ct);
            return;
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await _userStore.ResetLockoutAsync(user.TenantId, user.UserId, DateTimeOffset.UtcNow, onlyIfUnlocked: false, ct);
    }

    private Task LogLockoutAsync(User user, int threshold, string? ipAddress, string? userAgent, CancellationToken ct) =>
        _authEvents.LogAsync(
            user.TenantId.Value,
            user.UserId.Value,
            AuthEventTypes.Lockout,
            ipAddress,
            userAgent,
            new Dictionary<string, string> { ["threshold"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ct);

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

/// <summary>
/// A failed sign-in counted before its factor is checked (<see cref="AccountLockoutService.ReserveFailedAttemptAsync"/>).
/// </summary>
/// <param name="MayEvaluate">Whether the factor may be checked; otherwise the account is refused as locked.</param>
/// <param name="ReachedThreshold">Whether this count reached the lockout threshold, so the store locked the account.</param>
/// <param name="Threshold">The tenant's lockout threshold the count was compared with.</param>
internal readonly record struct FailedAttemptReservation(bool MayEvaluate, bool ReachedThreshold, int Threshold);
