using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>The vocabulary of the silent listen (design D1, D6, D10).</summary>
internal static class ListenVocabulary
{
    public const string Endpoint = "endpoint";
    public const string Phone = "phone";
    public const string Dialing = "dialing";
    public const string Listening = "listening";
    public const string Stopped = "stopped";
    public const string CallEnded = "call-ended";
    public const string NotAnswered = "not-answered";

    public static bool IsDestination(string? value) => value is Endpoint or Phone;
}

/// <summary>
/// One active listen (design D6). Its <see cref="ListenId"/> is also the supervisor leg's uniqueid: the originate sets
/// <c>ChannelId</c> to it. Mutable fields are written only under the registry's lock.
/// </summary>
internal sealed class ActiveListen
{
    public required string ListenId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId SupervisorUserId { get; init; }
    public required string ConversationId { get; init; }
    public required string Destination { get; init; }
    public required string AgentId { get; init; }
    public string? PhoneLast4 { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When ChanSpy started (the audio connected); null while <c>dialing</c>.</summary>
    public DateTimeOffset? ListeningSince { get; internal set; }

    /// <summary>The supervisor leg's channel name once its <c>Newchannel</c> is seen; stop needs it for <c>Hangup</c>.</summary>
    public string? ChannelName { get; internal set; }

    public string State => ListeningSince is null ? ListenVocabulary.Dialing : ListenVocabulary.Listening;

    /// <summary>Whole seconds of audio, from <see cref="ListeningSince"/> to <paramref name="endedAt"/>; 0 when it never connected.</summary>
    public int ListenedSeconds(DateTimeOffset endedAt) =>
        ListeningSince is { } since && endedAt > since ? (int)(endedAt - since).TotalSeconds : 0;
}

/// <summary>
/// The leader's in-memory registry of active listens, keyed by (tenant, supervisor): at most one per supervisor
/// (design D6). A listen leaves the registry exactly once (<see cref="TryEnd"/>), which is what makes
/// <c>supervision.listen_stopped</c> exactly-once whatever ends it. It also remembers the listens stopped before their
/// leg's channel name was known, so the observer can hang the leg up when it appears. Lost on a leadership failover
/// (accepted for the single-replica pilot).
/// </summary>
internal sealed class SupervisorListenRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ActiveListen> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string User), string> _bySupervisor = [];
    private readonly Dictionary<string, DateTimeOffset> _pendingHangups = new(StringComparer.Ordinal);

    // Listens that ended recently, kept so a late Newchannel / ChanSpyStart still updates the object a stop is holding
    // (a stop ends the listen first, then asks for its channel name). Pruned by the sweep.
    private readonly Dictionary<string, (ActiveListen Listen, DateTimeOffset EndedAt)> _ended = new(StringComparer.Ordinal);

    /// <summary>Reserves the supervisor's single slot; when it is taken, <paramref name="existing"/> names the holder.</summary>
    public bool TryAdd(ActiveListen listen, out ActiveListen? existing)
    {
        lock (_gate)
        {
            var key = (listen.TenantId.Value, listen.SupervisorUserId.Value);
            if (_bySupervisor.TryGetValue(key, out var id) && _byId.TryGetValue(id, out existing))
                return false;

            _bySupervisor[key] = listen.ListenId;
            _byId[listen.ListenId] = listen;
            existing = null;
            return true;
        }
    }

    public ActiveListen? GetBySupervisor(TenantId tenantId, EntityId userId)
    {
        lock (_gate)
            return _bySupervisor.TryGetValue((tenantId.Value, userId.Value), out var id) ? _byId.GetValueOrDefault(id) : null;
    }

    public ActiveListen? Get(string listenId)
    {
        lock (_gate)
            return _byId.GetValueOrDefault(listenId);
    }

    /// <summary>Removes the listen and returns it — to exactly one caller; later calls get <see langword="null"/>.</summary>
    public ActiveListen? TryEnd(string listenId, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            if (!_byId.Remove(listenId, out var listen))
                return null;
            _bySupervisor.Remove((listen.TenantId.Value, listen.SupervisorUserId.Value));
            _ended[listenId] = (listen, at ?? listen.StartedAt);
            return listen;
        }
    }

    /// <summary>Records the leg's channel name; returns <see langword="true"/> when a stop is waiting for it.</summary>
    public bool SetChannel(string listenId, string channelName)
    {
        lock (_gate)
        {
            if (Find(listenId) is { } listen)
                listen.ChannelName ??= channelName;
            return _pendingHangups.Remove(listenId);
        }
    }

    /// <summary>Moves the listen to <c>listening</c>; returns <see langword="true"/> when a stop is waiting for its leg.</summary>
    public bool MarkListening(string listenId, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(listenId, out var listen))
                listen.ListeningSince ??= at;
            return _pendingHangups.Remove(listenId);
        }
    }

    /// <summary>
    /// For a stop: the leg's channel name when it is known; otherwise records a pending hangup, atomically with
    /// <see cref="SetChannel"/>, so the observer hangs the leg up the moment it appears.
    /// </summary>
    public string? ChannelOrPendHangup(ActiveListen listen, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (listen.ChannelName is { } channel)
                return channel;
            _pendingHangups[listen.ListenId] = at;
            return null;
        }
    }

    private ActiveListen? Find(string listenId) =>
        _byId.TryGetValue(listenId, out var live) ? live : _ended.TryGetValue(listenId, out var ended) ? ended.Listen : null;

    /// <summary>Forgets a pending hangup (its leg ended on its own).</summary>
    public void ForgetPendingHangup(string listenId)
    {
        lock (_gate)
            _pendingHangups.Remove(listenId);
    }

    /// <summary>The <c>dialing</c> listens started before <paramref name="cutoff"/>.</summary>
    public IReadOnlyList<ActiveListen> StaleDialing(DateTimeOffset cutoff)
    {
        lock (_gate)
            return [.. _byId.Values.Where(l => l.ListeningSince is null && l.StartedAt < cutoff)];
    }

    /// <summary>Drops pending hangups and ended-listen tombstones recorded before <paramref name="cutoff"/>.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            foreach (var id in _pendingHangups.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _pendingHangups.Remove(id);
            foreach (var id in _ended.Where(kv => kv.Value.EndedAt < cutoff).Select(kv => kv.Key).ToList())
                _ended.Remove(id);
        }
    }

    public int PendingHangupCount
    {
        get
        {
            lock (_gate)
                return _pendingHangups.Count;
        }
    }
}
