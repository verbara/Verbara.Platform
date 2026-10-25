using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryTypificationSubmissionStore : ITypificationSubmissionStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), TypificationSubmission> _items = new();
    private readonly InMemoryDialerDispositionDispatchStore _dispatches;

    /// <summary>Creates the store over the dispatch store its <see cref="TryInsertAsync"/> adds rows to.</summary>
    /// <param name="dispatches">The dispatch store the dispatcher claims from; a private one when none is given.</param>
    public InMemoryTypificationSubmissionStore(InMemoryDialerDispositionDispatchStore? dispatches = null)
    {
        _dispatches = dispatches ?? new InMemoryDialerDispositionDispatchStore();
    }

    public Task SaveAsync(TypificationSubmission submission, CancellationToken ct)
    {
        _items[(submission.TenantId, submission.ConversationId)] = submission;
        return Task.CompletedTask;
    }

    public Task<TypificationSubmission?> GetByConversationIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, conversationId), out var item);
        return Task.FromResult(item);
    }

    /// <inheritdoc />
    /// <remarks>One <c>TryAdd</c> decides, as the Postgres insert-or-nothing does; the dispatch is added only when it won.</remarks>
    public Task<bool> TryInsertAsync(TypificationSubmission submission, DialerDispositionDispatch? dispatch, CancellationToken ct)
    {
        if (!_items.TryAdd((submission.TenantId, submission.ConversationId), submission))
            return Task.FromResult(false);

        if (dispatch is not null && !_dispatches.TryAdd(dispatch))
        {
            _items.TryRemove((submission.TenantId, submission.ConversationId), out _);
            throw new InvalidOperationException(
                $"Conversation {dispatch.ConversationId} already has a dialer disposition dispatch.");
        }

        return Task.FromResult(true);
    }
}
