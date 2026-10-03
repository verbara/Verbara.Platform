using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using NSubstitute;
using NSubstitute.Core;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Column writers for an <see cref="IUserStore"/> substitute that hands out ONE mutable
/// <see cref="User"/> instance (<see cref="AuthenticatedPlatformApiFactory"/>,
/// <see cref="AuthHandlerFixture"/>).
/// </summary>
internal static class SubstituteUserWrites
{
    /// <summary>
    /// Makes every column writer of the substitute act on <paramref name="user"/> itself — the ONE
    /// mutable instance every read of that user returns — with the preconditions the real stores
    /// enforce. Suites using such a substitute change that instance directly and read the
    /// endpoints' writes back from it; any other user id is "no such user".
    /// </summary>
    public static void ApplyTo(IUserStore userStore, User user)
    {
        bool IsUser(CallInfo ci) =>
            ci.ArgAt<TenantId>(0) == user.TenantId && ci.ArgAt<EntityId>(1) == user.UserId;

        Task<bool> Write(CallInfo ci, Func<bool> change) =>
            Task.FromResult(IsUser(ci) && change());

        userStore.UpdateProfileAsync(default, default, default!, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            var change = ci.ArgAt<UserProfileChange>(2);
            user.DisplayName = change.DisplayName ?? user.DisplayName;
            user.EmailVerified = change.EmailVerified ?? user.EmailVerified;
            user.AuthProvider = change.AuthProvider ?? user.AuthProvider;
            user.OidcSubject = change.OidcSubject ?? user.OidcSubject;
            user.UpdatedAt = ci.ArgAt<DateTimeOffset>(3);
            return true;
        }));
        userStore.UpdateAdminFieldsAsync(default, default, default!, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (!IsUser(ci))
                return Task.FromResult(AdminFieldsWriteResult.NotFound);
            var change = ci.ArgAt<AdminFieldsChange>(2);
            var previous = new AdminFields(user.DisplayName, user.Role, user.Status);
            if (change.Expected is { } expected && expected != previous)
                return Task.FromResult(AdminFieldsWriteResult.Stale);
            user.DisplayName = change.DisplayName ?? user.DisplayName;
            user.Role = change.Role ?? user.Role;
            user.Status = change.Status ?? user.Status;
            user.UpdatedAt = ci.ArgAt<DateTimeOffset>(3);
            user.UpdatedBy = ci.ArgAt<string?>(4) ?? user.UpdatedBy;
            return Task.FromResult(AdminFieldsWriteResult.Written(previous, user));
        });
        userStore.SetPasswordHashAsync(default, default, default!, default, default, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (ci.ArgAt<string?>(5) is { } expected && expected != user.PasswordHash)
                return false;
            user.PasswordHash = ci.ArgAt<string>(2);
            user.PasswordChangedAt = ci.ArgAt<DateTimeOffset>(3);
            if (ci.ArgAt<bool>(4))
            {
                user.FailedLoginAttempts = 0;
                user.LockedUntil = null;
            }
            return true;
        }));
        userStore.RehashPasswordAsync(default, default, default!, default!, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (ci.ArgAt<string>(2) != user.PasswordHash)
                return false;
            user.PasswordHash = ci.ArgAt<string>(3);
            return true;
        }));
        userStore.RecordFailedSignInAsync(default, default, default, default, default).ReturnsForAnyArgs(ci =>
        {
            if (!IsUser(ci))
                return Task.FromResult<FailedSignInResult?>(null);
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= ci.ArgAt<int>(2))
                user.LockedUntil = ci.ArgAt<DateTimeOffset>(3);
            return Task.FromResult<FailedSignInResult?>(new FailedSignInResult(user.FailedLoginAttempts, user.LockedUntil));
        });
        userStore.ResetLockoutAsync(default, default, default, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (ci.ArgAt<bool>(3) && user.LockedUntil > ci.ArgAt<DateTimeOffset>(2))
                return false;
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            return true;
        }));
        userStore.SetLastLoginAtAsync(default, default, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            var at = ci.ArgAt<DateTimeOffset>(2);
            if (user.LastLoginAt is null || user.LastLoginAt < at)
                user.LastLoginAt = at;
            return true;
        }));
        userStore.SetPendingMfaAsync(default, default, default!, default!, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (user.MfaEnabled)
                return false;
            user.MfaSecret = ci.ArgAt<string>(2);
            user.MfaRecoveryCodes = ci.ArgAt<IReadOnlyList<string>>(3).ToList();
            user.UpdatedAt = ci.ArgAt<DateTimeOffset>(4);
            return true;
        }));
        userStore.EnableMfaAsync(default, default, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (user.MfaEnabled || user.MfaSecret is null)
                return false;
            user.MfaEnabled = true;
            user.MfaConfirmedAt = ci.ArgAt<DateTimeOffset>(2);
            user.UpdatedAt = user.MfaConfirmedAt;
            return true;
        }));
        userStore.ClearMfaAsync(default, default, default, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            user.MfaEnabled = false;
            user.MfaSecret = null;
            user.MfaRecoveryCodes = null;
            user.MfaConfirmedAt = null;
            user.UpdatedAt = ci.ArgAt<DateTimeOffset>(3);
            if (ci.ArgAt<bool>(2))
            {
                user.FailedLoginAttempts = 0;
                user.LockedUntil = null;
            }
            return true;
        }));
        userStore.SetRecoveryCodesAsync(default, default, default!, default, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            if (!user.MfaEnabled)
                return false;
            user.MfaRecoveryCodes = ci.ArgAt<IReadOnlyList<string>>(2).ToList();
            user.UpdatedAt = ci.ArgAt<DateTimeOffset>(3);
            return true;
        }));
        userStore.ConsumeRecoveryCodeAsync(default, default, default!, default).ReturnsForAnyArgs(ci => Write(ci, () =>
        {
            var digest = ci.ArgAt<string>(2);
            if (user.MfaRecoveryCodes is not { } codes || !codes.Contains(digest))
                return false;
            user.MfaRecoveryCodes = codes.Where(c => c != digest).ToList();
            return true;
        }));
    }
}
