using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>Persistence abstraction for completed typification submissions.</summary>
public interface ITypificationSubmissionStore
{
    Task SaveAsync(TypificationSubmission submission, CancellationToken ct);

    Task<TypificationSubmission?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
}
