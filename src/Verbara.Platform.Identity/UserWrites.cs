namespace Verbara.Platform.Identity;

/// <summary>
/// The profile fields an identity-provider sign-in may change on an existing user, for
/// <see cref="IUserStore.UpdateProfileAsync"/>. A <see langword="null"/> field is left as stored.
/// </summary>
public sealed record UserProfileChange
{
    public string? DisplayName { get; init; }
    public bool? EmailVerified { get; init; }
    public string? AuthProvider { get; init; }
    public string? OidcSubject { get; init; }
}

/// <summary>The three fields an administrator edits on a user, as one row holds them.</summary>
public readonly record struct AdminFields(string DisplayName, UserRole Role, UserStatus Status);

/// <summary>
/// An administrator's change to <see cref="AdminFields"/>, for
/// <see cref="IUserStore.UpdateAdminFieldsAsync"/>. A <see langword="null"/> field is left as stored.
/// </summary>
public sealed record AdminFieldsChange
{
    public string? DisplayName { get; init; }
    public UserRole? Role { get; init; }
    public UserStatus? Status { get; init; }

    /// <summary>
    /// When set, the change is written only while the row still holds exactly these values;
    /// otherwise the write is refused with <see cref="AdminFieldsWriteOutcome.Stale"/>.
    /// </summary>
    public AdminFields? Expected { get; init; }
}

/// <summary>What <see cref="IUserStore.UpdateAdminFieldsAsync"/> did.</summary>
public enum AdminFieldsWriteOutcome
{
    /// <summary>The change was written.</summary>
    Written,

    /// <summary>There is no such user; nothing was written or created.</summary>
    NotFound,

    /// <summary>The row no longer held <see cref="AdminFieldsChange.Expected"/>; nothing was written.</summary>
    Stale,
}

/// <summary>
/// The result of <see cref="IUserStore.UpdateAdminFieldsAsync"/>. On
/// <see cref="AdminFieldsWriteOutcome.Written"/>, <see cref="Previous"/> holds the values the write
/// replaced — read under the row lock, so a concurrent writer's committed change is what comes back —
/// and <see cref="User"/> the user as stored after it.
/// </summary>
public sealed record AdminFieldsWriteResult(AdminFieldsWriteOutcome Outcome, AdminFields? Previous, User? User)
{
    public static AdminFieldsWriteResult NotFound { get; } = new(AdminFieldsWriteOutcome.NotFound, null, null);

    public static AdminFieldsWriteResult Stale { get; } = new(AdminFieldsWriteOutcome.Stale, null, null);

    public static AdminFieldsWriteResult Written(AdminFields previous, User user) =>
        new(AdminFieldsWriteOutcome.Written, previous, user);
}

/// <summary>The failed-attempt count and lock a failed sign-in left on the row.</summary>
public readonly record struct FailedSignInResult(int FailedAttempts, DateTimeOffset? LockedUntil);
