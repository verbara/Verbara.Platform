using Verbara.Platform.Queues;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// What <see cref="InMemoryConversationStore.ListContactSummariesAsync"/> joins to a conversation, as the Postgres
/// summary query joins <c>queue_configs</c>, <c>agents</c>, <c>typification_submissions</c> and
/// <c>typification_submission_corrections</c> (contact-history-opens-by-assignment, design D6). Each store is resolved
/// at first use, so building the conversation store never builds the others.
/// </summary>
internal sealed record InMemoryContactSummarySources(
    Func<IQueueStore> Queues,
    Func<IAgentStore> Agents,
    Func<ITypificationSubmissionStore> Submissions,
    Func<ITypificationSubmissionCorrectionStore> Corrections);
