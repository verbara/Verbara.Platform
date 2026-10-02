namespace Verbara.Platform.Core.Push;

/// <summary>
/// The live connections each account holds on this node — Realtime hub connections, Api SSE
/// streams — so they can be cut the moment the account loses access. Authentication happens once
/// when such a connection opens; without this index nothing could end it before the credential
/// that opened it expires (<see cref="LiveConnectionExpiry"/>).
/// </summary>
/// <remarks>
/// <para>
/// A connection registers an abort callback when it opens and disposes the returned handle when it
/// ends. <see cref="AbortAll"/> removes and aborts everything registered for an account, so a
/// revocation delivered twice (typed re-publish plus backplane envelope) aborts each connection once.
/// </para>
/// <para>
/// Register BEFORE checking the account's status: a revocation that lands between the check and
/// the registration would otherwise miss the connection. With registration first, either the
/// revocation finds the connection, or the status check (which reads the already-persisted
/// status) refuses it.
/// </para>
/// </remarks>
public sealed class LiveConnectionRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LiveConnectionOwner, Dictionary<long, Action>> _byOwner = [];
    private long _nextId;

    /// <summary>Tracks a live connection of an account until the returned handle is disposed.</summary>
    /// <param name="tenantId">The account's tenant.</param>
    /// <param name="userId">The account.</param>
    /// <param name="abort">Ends the connection. Called at most once, from any thread.</param>
    public IDisposable Register(string tenantId, string userId, Action abort)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentNullException.ThrowIfNull(abort);

        var owner = new LiveConnectionOwner(tenantId, userId);
        lock (_gate)
        {
            var id = ++_nextId;
            if (!_byOwner.TryGetValue(owner, out var connections))
                _byOwner[owner] = connections = [];
            connections[id] = abort;
            return new Registration(this, owner, id);
        }
    }

    /// <summary>Aborts and forgets every live connection the account holds on this node.</summary>
    /// <returns>How many connections were aborted; 0 when the account held none.</returns>
    /// <exception cref="AggregateException">
    /// An abort callback failed. Every other connection was still aborted first.
    /// </exception>
    public int AbortAll(string tenantId, string userId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);

        List<Action> aborts;
        lock (_gate)
        {
            if (!_byOwner.Remove(new LiveConnectionOwner(tenantId, userId), out var connections))
                return 0;
            aborts = [.. connections.Values];
        }

        List<Exception>? failures = null;
        foreach (var abort in aborts)
        {
            try
            {
                abort();
            }
            catch (ObjectDisposedException)
            {
                // The connection finished tearing down while being aborted: it is gone, which is
                // exactly what the abort wanted.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Keep going: one connection that fails to abort must not shield the others.
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more live connections failed to abort.", failures);

        return aborts.Count;
    }

    private void Unregister(LiveConnectionOwner owner, long id)
    {
        lock (_gate)
        {
            // Ids are never reused, so a stale handle (its owner already aborted and re-registered
            // since) finds nothing to remove.
            if (_byOwner.TryGetValue(owner, out var connections) && connections.Remove(id) && connections.Count == 0)
                _byOwner.Remove(owner);
        }
    }

    private sealed class Registration(LiveConnectionRegistry registry, LiveConnectionOwner owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Unregister(owner, id);
        }
    }
}
