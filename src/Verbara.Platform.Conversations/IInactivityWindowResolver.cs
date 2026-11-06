using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

/// <summary>
/// The one reading of a tenant's effective inactivity window (conversations-close-after-inactivity D11), shared by the
/// inactivity close and by last-agent routing's clamp (conversations-return-to-last-agent design D4, ruling Q12), so the
/// two cannot drift: the tenant's stored <c>inactivity_close_after_hours</c> when it is valid, else the host's
/// <see cref="ConversationOptions.InactivityCloseAfterHours"/>.
/// </summary>
public interface IInactivityWindowResolver
{
    /// <summary>
    /// Resolves <paramref name="tenantId"/>'s effective window. Throws when the tenant's override cannot be read: the
    /// caller decides what a failed read means (the inactivity close skips the tenant this pass; routing counts
    /// <c>lookup-failed</c>).
    /// </summary>
    Task<InactivityWindowResolution> ResolveAsync(TenantId tenantId, CancellationToken ct);
}

/// <summary>
/// A tenant's effective inactivity window, in hours, and what the tenant's stored override did to it.
/// </summary>
/// <param name="Hours">The effective window: the valid override, else the host window.</param>
/// <param name="StoredOverrideHours">The tenant's stored override, or <see langword="null"/> when it has none.</param>
/// <param name="OverrideApplied">
/// Whether <paramref name="StoredOverrideHours"/> is the window; <see langword="false"/> with a stored value means the
/// value was below the minimum and the host window applies.
/// </param>
public readonly record struct InactivityWindowResolution(int Hours, int? StoredOverrideHours, bool OverrideApplied);
