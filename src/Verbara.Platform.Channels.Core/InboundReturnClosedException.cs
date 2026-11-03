using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// conversations-close-after-inactivity (audit round 4, F1): the inbound pipeline failed after this delivery had
/// already committed a <c>Resolved → Closed</c> return close (design D8). The close cannot be undone and a retry of
/// the delivery finds the row closed, so it would never be announced. The pipeline therefore wraps the failure in this
/// exception, carrying the closed id; the caller announces the close and rethrows <see cref="Exception.InnerException"/>,
/// so the delivery still fails exactly as it would have (a provider retries, a client sees the same error).
/// </summary>
public sealed class InboundReturnClosedException : Exception
{
    public InboundReturnClosedException()
    {
    }

    public InboundReturnClosedException(string message)
        : base(message)
    {
    }

    public InboundReturnClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InboundReturnClosedException(TenantId tenantId, EntityId closedOnReturnId, Exception innerException)
        : base($"Inbound message processing failed after the return close of conversation '{closedOnReturnId}'.", innerException)
    {
        TenantId = tenantId;
        ClosedOnReturnId = closedOnReturnId;
    }

    /// <summary>The tenant of the closed conversation.</summary>
    public TenantId TenantId { get; }

    /// <summary>The <c>Resolved</c> conversation this delivery closed before it failed.</summary>
    public EntityId ClosedOnReturnId { get; }
}
