using Microsoft.IdentityModel.Tokens;

namespace Verbara.Platform.Identity.Auth.Jwt;

/// <summary>
/// The validation keys a token check reads, held in memory and reloaded from the key pool off the
/// request path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Resolve"/> is what <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>
/// calls, synchronously, inside a request. It only reads the current snapshot: it never waits on the
/// pool. When the snapshot is due for a refresh, or a token names a key id the snapshot does not hold,
/// it starts a reload in the background and answers from what it has.
/// </para>
/// <para>
/// <see cref="EnsureLoadedAsync(string?, CancellationToken)"/> awaits the first load asynchronously;
/// the JwtBearer <c>OnMessageReceived</c> event calls it with the key id the token names, so a
/// replica's first requests wait for the keys without holding a thread, and a token signed with a key
/// the snapshot does not hold yet (the first key setup creates, a rotated key) waits for one reload
/// instead of being refused. It is never called at host start, so a host boots without the pool.
/// </para>
/// <para>
/// Reload rules: one at a time; after a failure the next attempt waits (1 s, doubling, at most
/// <see cref="JwtValidationKeySnapshotOptions.MaxFailureBackoff"/>) and the previous snapshot keeps
/// serving. A reload for an unknown key id starts at most once per
/// <see cref="JwtValidationKeySnapshotOptions.UnknownKeyIdReloadInterval"/>, also inside a failure
/// backoff; requests that arrive meanwhile share the next one, which reads the whole pool, so forged
/// key ids cannot keep a real one out for longer than that interval. A pool with no live key, read
/// while the snapshot holds none, is a successful empty load (a fresh install before setup), not a
/// failure: it is read again after <see cref="JwtValidationKeySnapshotOptions.InitialFailureBackoff"/>
/// without doubling. A reload that leaves out keys the previous snapshot held and that have not
/// expired (an empty read included) keeps them, and counts as failed so it is retried after the
/// backoff.
/// </para>
/// </remarks>
public sealed class JwtValidationKeySnapshot
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<JwtKeyEntry>>> _load;
    private readonly Func<JwtKeyEntry, SecurityKey> _build;
    private readonly JwtValidationKeySnapshotOptions _options;
    private readonly TimeProvider _time;
    private readonly JwtValidationKeySnapshotEvents _events;

    private Snapshot? _current;
    private Task? _inFlight;
    private Task? _unreadReload;               // the in-flight reload until it starts reading the pool
    private long _nextReloadAtTicks;          // refresh due (after a success) or backoff end (after a failure)
    private int _consecutiveFailures;          // written only by the single in-flight reload

    private readonly Lock _unknownKeyIdGate = new();
    private Task? _pendingUnknownKeyIdReload;  // scheduled, has not read the pool yet; under the gate
    private long _lastUnknownKeyIdReloadTicks = long.MinValue; // under the gate

    /// <summary>Creates a snapshot that loads its keys with <paramref name="load"/>.</summary>
    /// <param name="load">Reads every key of the pool. Called off the request path, one call at a time.</param>
    /// <param name="build">Turns a pool entry into the key the token handler checks signatures with.</param>
    /// <param name="options">Refresh, backoff and wait intervals; defaults when <see langword="null"/>.</param>
    /// <param name="timeProvider">Clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <param name="events">Observability hooks; none when <see langword="null"/>.</param>
    public JwtValidationKeySnapshot(
        Func<CancellationToken, Task<IReadOnlyList<JwtKeyEntry>>> load,
        Func<JwtKeyEntry, SecurityKey> build,
        JwtValidationKeySnapshotOptions? options = null,
        TimeProvider? timeProvider = null,
        JwtValidationKeySnapshotEvents? events = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(build);
        _load = load;
        _build = build;
        _options = options ?? new JwtValidationKeySnapshotOptions();
        _time = timeProvider ?? TimeProvider.System;
        _events = events ?? new JwtValidationKeySnapshotEvents();
        _nextReloadAtTicks = DateTimeOffset.MinValue.UtcTicks;
    }

    /// <summary>Whether a full load of the pool has succeeded at least once.</summary>
    public bool IsLoaded => Volatile.Read(ref _current) is { Complete: true };

    /// <summary>The reload running now, or a completed task. For tests and diagnostics.</summary>
    public Task ReloadInFlight => Volatile.Read(ref _inFlight) ?? Task.CompletedTask;

    /// <summary>The unknown-key-id reload scheduled for later, or a completed task. For tests.</summary>
    internal Task PendingUnknownKeyIdReload
    {
        get
        {
            lock (_unknownKeyIdGate)
                return _pendingUnknownKeyIdReload ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// The keys to check a token against. Never waits: starts a background reload when one is due or
    /// <paramref name="keyId"/> is unknown, and answers from the current snapshot (empty before the
    /// first load).
    /// </summary>
    public IReadOnlyList<SecurityKey> Resolve(string? keyId)
    {
        var snapshot = Volatile.Read(ref _current);
        var nowTicks = _time.GetUtcNow().UtcTicks;

        if (nowTicks >= Volatile.Read(ref _nextReloadAtTicks))
        {
            _ = StartReload();
        }
        else if (!string.IsNullOrEmpty(keyId) && !Holds(snapshot, keyId))
        {
            _ = RequestUnknownKeyIdReload();
        }

        return snapshot?.Keys ?? [];
    }

    /// <summary>
    /// <see cref="EnsureLoadedAsync(string?, CancellationToken)"/> for a token whose key id is not
    /// known.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => EnsureLoadedAsync(null, cancellationToken);

    /// <summary>
    /// Awaits the first full load, at most <see cref="JwtValidationKeySnapshotOptions.FirstLoadWait"/>;
    /// once loaded, only starts a background refresh when one is due. When <paramref name="keyId"/>
    /// names a key the snapshot does not hold, then awaits one reload that reads the pool after this
    /// call, rate-limited and shared with every other request waiting for one, at most
    /// <see cref="JwtValidationKeySnapshotOptions.UnknownKeyIdWait"/>. Never throws for a pool failure:
    /// the token check that follows fails on its own when no key is held.
    /// </summary>
    /// <param name="keyId">The <c>kid</c> the request's token names (<see cref="JwtKeyId"/>), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The request's abort token.</param>
    public async Task EnsureLoadedAsync(string? keyId, CancellationToken cancellationToken)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var due = nowTicks >= Volatile.Read(ref _nextReloadAtTicks);

        if (IsLoaded)
        {
            if (due)
                _ = StartReload();
        }
        else if (due || Volatile.Read(ref _inFlight) is not null)
        {
            // Not loaded yet. Inside a failure backoff this is skipped rather than queue requests.
            try
            {
                await StartReload().WaitAsync(_options.FirstLoadWait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The load keeps running in the background; this request goes on without keys.
            }
        }

        if (string.IsNullOrEmpty(keyId) || Holds(Volatile.Read(ref _current), keyId))
            return;

        try
        {
            await RequestUnknownKeyIdReload()
                .WaitAsync(_options.UnknownKeyIdWait, _time, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reload goes on in the background; the token check that follows refuses this token.
        }
    }

    /// <summary>
    /// Adds a key this replica has just read from the pool (the signing path's active key) without
    /// waiting for the next reload. Never removes a key.
    /// </summary>
    public void Add(JwtKeyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = _build(entry);
        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (current is not null && current.ById.ContainsKey(entry.KeyId))
                return;

            var items = new Dictionary<string, Item>(StringComparer.Ordinal);
            if (current is not null)
            {
                foreach (var pair in current.ById)
                    items[pair.Key] = pair.Value;
            }
            items[entry.KeyId] = new Item(entry.ExpiresAt, key);
            var next = new Snapshot(items, current?.Complete ?? false);
            if (Interlocked.CompareExchange(ref _current, next, current) == current)
                return;
        }
    }

    /// <summary>Starts a reload unless one is running; returns the running one.</summary>
    private Task StartReload()
    {
        var running = Volatile.Read(ref _inFlight);
        if (running is not null)
            return running;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running = Interlocked.CompareExchange(ref _inFlight, done.Task, null);
        if (running is not null)
            return running;

        Volatile.Write(ref _unreadReload, done.Task);
        // Off the caller's thread: a loader that blocks before its first await must not block a request.
        _ = Task.Run(() => RunReloadAsync(done));
        return done.Task;
    }

    private static bool Holds(Snapshot? snapshot, string keyId) =>
        snapshot is not null && snapshot.ById.ContainsKey(keyId);

    /// <summary>
    /// A reload that reads the pool after this call, for a token whose key id the snapshot does not
    /// hold. Joins a reload that has not read the pool yet; starts one now when the rate limit allows
    /// and no reload is running; otherwise returns the one already scheduled, or schedules one for
    /// when the interval ends and any running reload (which may have read the pool before this call)
    /// has finished. Completes when it has run.
    /// </summary>
    private Task RequestUnknownKeyIdReload()
    {
        TaskCompletionSource scheduled;
        Task delay;
        lock (_unknownKeyIdGate)
        {
            if (_pendingUnknownKeyIdReload is { } pending)
                return pending;
            // A reload that has not read the pool yet reads it after this call: share it, at no cost.
            if (Volatile.Read(ref _unreadReload) is { } unread)
                return unread;

            var nowTicks = _time.GetUtcNow().UtcTicks;
            var last = _lastUnknownKeyIdReloadTicks;
            var earliest = last == long.MinValue ? nowTicks : last + _options.UnknownKeyIdReloadInterval.Ticks;
            if (earliest <= nowTicks && Volatile.Read(ref _inFlight) is null)
                return StartUnknownKeyIdReload(nowTicks);

            scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingUnknownKeyIdReload = scheduled.Task;
            // The timer is taken here, on the caller's clock reading, not later on a pool thread.
            delay = earliest > nowTicks ? Task.Delay(TimeSpan.FromTicks(earliest - nowTicks), _time) : Task.CompletedTask;
        }

        _ = Task.Run(() => RunScheduledUnknownKeyIdReloadAsync(scheduled, delay));
        return scheduled.Task;
    }

    private async Task RunScheduledUnknownKeyIdReloadAsync(TaskCompletionSource scheduled, Task delay)
    {
        try
        {
            await delay.ConfigureAwait(false);
            // A reload running now may have read the pool before the request that asked arrived.
            await ReloadInFlight.ConfigureAwait(false);
            Task reload;
            lock (_unknownKeyIdGate)
            {
                _pendingUnknownKeyIdReload = null;
                reload = StartUnknownKeyIdReload(_time.GetUtcNow().UtcTicks);
            }
            await reload.ConfigureAwait(false);
        }
        finally
        {
            scheduled.TrySetResult();
        }
    }

    /// <summary>Starts (or joins, when one started since the caller looked) a reload; under the gate.</summary>
    private Task StartUnknownKeyIdReload(long nowTicks)
    {
        _lastUnknownKeyIdReloadTicks = nowTicks;
        _events.UnknownKeyIdReload?.Invoke();
        return StartReload();
    }

    private async Task RunReloadAsync(TaskCompletionSource done)
    {
        var baseline = Volatile.Read(ref _current);
        try
        {
            _events.ReloadStarted?.Invoke();
            _ = Interlocked.CompareExchange(ref _unreadReload, null, done.Task);
            var entries = await _load(CancellationToken.None)
                .WaitAsync(_options.LoadTimeout, _time)
                .ConfigureAwait(false);

            var now = _time.GetUtcNow();
            var loaded = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e.ExpiresAt > now))
            {
                // Reuse the key already built for this id: an RSA import per reload is wasted work.
                loaded[entry.KeyId] = baseline is not null && baseline.ById.TryGetValue(entry.KeyId, out var known)
                    ? known with { ExpiresAt = entry.ExpiresAt }
                    : new Item(entry.ExpiresAt, _build(entry));
            }

            // No live key, while none is held either: the pool is empty (a fresh install before setup
            // creates the first key) or a read skipped it; both serve no key. Not a failure, so no
            // growing backoff: read again soon, and at once (rate-limited) for a token naming a key.
            // While keys are held, an empty read is a partial read: the left-out rule below keeps them.
            var emptyLoad = loaded.Count == 0;

            var kept = 0;
            if (baseline is not null)
            {
                var leftOut = baseline.ById
                    .Where(p => p.Value.ExpiresAt > now && !loaded.ContainsKey(p.Key))
                    .ToList();
                foreach (var pair in leftOut)
                    loaded.Add(pair.Key, pair.Value);
                kept = leftOut.Count;
            }

            Publish(loaded, now);

            if (kept > 0)
            {
                throw new JwtKeySetRejectedException(
                    $"The key pool returned {loaded.Count - kept} live keys but left out {kept} unexpired keys held from the previous load; keeping them.");
            }

            Volatile.Write(ref _consecutiveFailures, 0);
            var refresh = emptyLoad ? _options.InitialFailureBackoff : _options.RefreshInterval;
            Volatile.Write(ref _nextReloadAtTicks, now.Add(refresh).UtcTicks);
            _events.ReloadSucceeded?.Invoke(loaded.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A reload failure of any other kind (timeout, Redis error, rejected key set, a key that does
            // not parse) keeps the previous snapshot; it never reaches a request.
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            var backoff = Backoff(failures);
            Volatile.Write(ref _nextReloadAtTicks, _time.GetUtcNow().Add(backoff).UtcTicks);
            _events.ReloadFailed?.Invoke(ex, IsLoaded);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _unreadReload, null, done.Task);
            Volatile.Write(ref _inFlight, null);
            done.TrySetResult();
        }
    }

    /// <summary>Publishes the loaded set, keeping any key <see cref="Add"/> put in meanwhile.</summary>
    private void Publish(Dictionary<string, Item> loaded, DateTimeOffset now)
    {
        while (true)
        {
            var current = Volatile.Read(ref _current);
            var items = new Dictionary<string, Item>(loaded, StringComparer.Ordinal);
            if (current is not null)
            {
                foreach (var pair in current.ById.Where(p => p.Value.ExpiresAt > now))
                    items.TryAdd(pair.Key, pair.Value);
            }
            var next = new Snapshot(items, complete: true);
            if (Interlocked.CompareExchange(ref _current, next, current) == current)
                return;
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Pow(2, Math.Min(failures - 1, 16)) * _options.InitialFailureBackoff.TotalSeconds;
        var backoff = TimeSpan.FromSeconds(seconds);
        return backoff < _options.MaxFailureBackoff ? backoff : _options.MaxFailureBackoff;
    }

    private sealed record Item(DateTimeOffset ExpiresAt, SecurityKey Key);

    private sealed class Snapshot
    {
        public Snapshot(Dictionary<string, Item> byId, bool complete)
        {
            ById = byId;
            Complete = complete;
            Keys = byId.Values.Select(i => i.Key).ToArray();
        }

        public Dictionary<string, Item> ById { get; }
        public bool Complete { get; }
        public SecurityKey[] Keys { get; }
    }
}

/// <summary>Intervals for <see cref="JwtValidationKeySnapshot"/>.</summary>
public sealed record JwtValidationKeySnapshotOptions
{
    /// <summary>How long a loaded snapshot serves before a background refresh. Default 5 minutes.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Least time between two reloads triggered by an unknown key id. Default 2 seconds: also the
    /// longest a token signed with a new key waits when forged key ids took the last reload.
    /// </summary>
    public TimeSpan UnknownKeyIdReloadInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Longest a request whose token names an unknown key id waits for the reload it asked for.
    /// Default 5 seconds. Before the first load, the same request first waits up to
    /// <see cref="FirstLoadWait"/> for that load, so it can wait both: up to 10 seconds with the
    /// defaults.
    /// </summary>
    public TimeSpan UnknownKeyIdWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wait after the first failed reload; doubles per failure. Default 1 second.</summary>
    public TimeSpan InitialFailureBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between failed reloads. Default 30 seconds.</summary>
    public TimeSpan MaxFailureBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest a request waits for the first load. Default 5 seconds.</summary>
    public TimeSpan FirstLoadWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest one reload may take before it counts as failed. Default 30 seconds.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Observability hooks for <see cref="JwtValidationKeySnapshot"/>; each may be <see langword="null"/>.</summary>
public sealed class JwtValidationKeySnapshotEvents
{
    /// <summary>A reload starts (one read of the pool).</summary>
    public Action? ReloadStarted { get; init; }

    /// <summary>A reload succeeded with this many live keys.</summary>
    public Action<int>? ReloadSucceeded { get; init; }

    /// <summary>
    /// A reload failed. The flag says whether a loaded snapshot keeps serving (<see langword="true"/>)
    /// or none was ever loaded.
    /// </summary>
    public Action<Exception, bool>? ReloadFailed { get; init; }

    /// <summary>A token named a key id the snapshot does not hold, and a reload was started for it.</summary>
    public Action? UnknownKeyIdReload { get; init; }
}

/// <summary>A reload's key set was not accepted as the pool's whole content.</summary>
public sealed class JwtKeySetRejectedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JwtKeySetRejectedException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public JwtKeySetRejectedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public JwtKeySetRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
