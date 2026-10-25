using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// The in-memory twin of the dialer disposition dispatches (design D16). One lock serializes every operation, which
/// gives the claim, the fenced completion and the fenced release the same all-or-nothing behaviour as the Postgres
/// statements. <see cref="InMemoryTypificationSubmissionStore"/> adds rows through <see cref="TryAdd"/>.
/// </summary>
internal sealed class InMemoryDialerDispositionDispatchStore : IDialerDispositionDispatchStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string TenantId, string ConversationId), DialerDispositionDispatch> _rows = [];

    /// <summary>Adds <paramref name="dispatch"/> unless its Conversation already has one; reports whether it did.</summary>
    internal bool TryAdd(DialerDispositionDispatch dispatch)
    {
        lock (_gate)
            return _rows.TryAdd((dispatch.TenantId, dispatch.ConversationId), dispatch);
    }

    /// <summary>Removes the row of the Conversation (rolls back a <see cref="TryAdd"/> whose submission was refused).</summary>
    internal void Remove(string tenantId, string conversationId)
    {
        lock (_gate)
            _rows.Remove((tenantId, conversationId));
    }

    /// <summary>The row of the Conversation, as stored (tests and diagnostics).</summary>
    internal DialerDispositionDispatch? Get(string tenantId, string conversationId)
    {
        lock (_gate)
            return _rows.GetValueOrDefault((tenantId, conversationId));
    }

    public Task<IReadOnlyList<DialerDispositionDispatch>> ClaimDueAsync(
        int limit, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var claimed = new List<DialerDispositionDispatch>();
        lock (_gate)
        {
            var due = _rows.Values
                .Where(r => r.Result is null && r.NextAttemptAt <= now
                            && (r.ClaimId is null || r.ClaimExpiresAt is null || r.ClaimExpiresAt <= now))
                .OrderBy(r => r.NextAttemptAt)
                .Take(limit)
                .ToList();
            foreach (var row in due)
            {
                var next = row with
                {
                    ClaimId = Guid.NewGuid().ToString("N"),
                    ClaimExpiresAt = expiresAt,
                    Attempts = row.Attempts + 1,
                };
                _rows[(row.TenantId, row.ConversationId)] = next;
                claimed.Add(next);
            }
        }

        return Task.FromResult<IReadOnlyList<DialerDispositionDispatch>>(claimed);
    }

    public Task<bool> CompleteAsync(
        DialerDispositionDispatch dispatch, string result, bool published, string claimId, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = (dispatch.TenantId, dispatch.ConversationId);
            if (!_rows.TryGetValue(key, out var row) || row.ClaimId != claimId)
                return Task.FromResult(false);

            var now = DateTimeOffset.UtcNow;
            _rows[key] = row with
            {
                Result = result,
                CompletedAt = now,
                PublishedAt = published ? now : null,
                ClaimId = null,
                ClaimExpiresAt = null,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(
        DialerDispositionDispatch dispatch, string lastError, DateTimeOffset nextAttemptAt, string claimId, CancellationToken ct)
    {
        lock (_gate)
        {
            var key = (dispatch.TenantId, dispatch.ConversationId);
            if (!_rows.TryGetValue(key, out var row) || row.ClaimId != claimId)
                return Task.FromResult(false);

            _rows[key] = row with
            {
                LastError = lastError,
                NextAttemptAt = nextAttemptAt,
                ClaimId = null,
                ClaimExpiresAt = null,
            };
            return Task.FromResult(true);
        }
    }

    public Task<long> CountPendingAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult((long)_rows.Values.Count(r => r.Result is null));
    }
}
