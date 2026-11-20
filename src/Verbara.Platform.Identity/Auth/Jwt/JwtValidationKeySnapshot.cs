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
/// <see cref="EnsureLoadedAsync"/> awaits the first load asynchronously; the JwtBearer
/// <c>OnMessageReceived</c> event calls it, so a replica's first requests wait for the keys without
/// holding a thread. It is never called at host start, so a host boots without the pool.
/// </para>
/// <para>
/// Reload rules: one at a time; after a failure the next attempt waits (1 s, doubling, at most
/// <see cref="JwtValidationKeySnapshotOptions.MaxFailureBackoff"/>) and the previous snapshot keeps
/// serving; a reload for an unknown key id runs at most once per
/// <see cref="JwtValidationKeySnapshotOptions.UnknownKeyIdReloadInterval"/>. A reload that returns no
/// live key is a failed reload. A reload that leaves out keys the previous snapshot held and that have
/// not expired keeps them, and counts as failed so it is retried after the backoff.
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
    private long _nextReloadAtTicks;          // refresh due (after a success) or backoff end (after a failure)
    private long _lastUnknownKeyIdReloadTicks = long.MinValue;
    private int _consecutiveFailures;          // written only by the single in-flight reload

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
        else if (!string.IsNullOrEmpty(keyId)
                 && snapshot is not null
                 && !snapshot.ById.ContainsKey(keyId)
                 && Volatile.Read(ref _consecutiveFailures) == 0)
        {
            TryStartUnknownKeyIdReload(nowTicks);
        }

        return snapshot?.Keys ?? [];
    }

    /// <summary>
    /// Awaits the first full load, at most <see cref="JwtValidationKeySnapshotOptions.FirstLoadWait"/>;
    /// once loaded, only starts a background refresh when one is due. Never throws for a pool failure:
    /// the token check that follows fails on its own when no key is held.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        var nowTicks = _time.GetUtcNow().UtcTicks;
        var due = nowTicks >= Volatile.Read(ref _nextReloadAtTicks);

        if (IsLoaded)
        {
            if (due)
                _ = StartReload();
            return;
        }

        // Not loaded yet. Inside a failure backoff, answer at once rather than queue requests.
        if (!due && Volatile.Read(ref _inFlight) is null)
            return;

        try
        {
            await StartReload().WaitAsync(_options.FirstLoadWait, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The load keeps running in the background; this request goes on without keys.
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

        // Off the caller's thread: a loader that blocks before its first await must not block a request.
        _ = Task.Run(() => RunReloadAsync(done));
        return done.Task;
    }

    private void TryStartUnknownKeyIdReload(long nowTicks)
    {
        var last = Interlocked.Read(ref _lastUnknownKeyIdReloadTicks);
        if (last != long.MinValue && nowTicks - last < _options.UnknownKeyIdReloadInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastUnknownKeyIdReloadTicks, nowTicks, last) != last)
            return;
        _events.UnknownKeyIdReload?.Invoke();
        _ = StartReload();
    }

    private async Task RunReloadAsync(TaskCompletionSource done)
    {
        var baseline = Volatile.Read(ref _current);
        try
        {
            _events.ReloadStarted?.Invoke();
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

            if (loaded.Count == 0)
                throw new JwtKeySetRejectedException("The key pool returned no live key; keeping the keys already held.");

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
            Volatile.Write(ref _nextReloadAtTicks, now.Add(_options.RefreshInterval).UtcTicks);
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

    /// <summary>Least time between two reloads triggered by an unknown key id. Default 10 seconds.</summary>
    public TimeSpan UnknownKeyIdReloadInterval { get; init; } = TimeSpan.FromSeconds(10);

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
