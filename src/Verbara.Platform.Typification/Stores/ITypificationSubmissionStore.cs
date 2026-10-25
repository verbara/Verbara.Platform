using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>Persistence abstraction for completed typification submissions.</summary>
public interface ITypificationSubmissionStore
{
    Task SaveAsync(TypificationSubmission submission, CancellationToken ct);

    Task<TypificationSubmission?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="submission"/> only when its Conversation has none yet, and with it, in the same
    /// transaction, the <paramref name="dispatch"/> it owes the dialer when one is given (design D15, D16, D17). An
    /// insert-or-nothing on the Conversation: the first submission stored wins, whoever writes it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when both were stored; <see langword="false"/> when a submission was already stored, in
    /// which case nothing was written. A failure of either write stores neither.
    /// </returns>
    Task<bool> TryInsertAsync(TypificationSubmission submission, DialerDispositionDispatch? dispatch, CancellationToken ct);
}
