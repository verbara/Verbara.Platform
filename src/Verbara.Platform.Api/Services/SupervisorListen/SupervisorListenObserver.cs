using Verbara.Platform.Core;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (design D6, D10): follows each listen's own leg on the AMI stream. The originate sets
/// <c>ChannelId = listenId</c>, so the leg's uniqueid IS the listen id and nothing is guessed:
/// <list type="bullet">
///   <item><c>Newchannel</c> with <c>Uniqueid = listenId</c> records the channel name a stop needs for <c>Hangup</c>;</item>
///   <item><c>ChanSpyStart</c> with <c>SpyerUniqueid = listenId</c> moves the listen to <c>listening</c>;</item>
///   <item><c>Hangup</c> with <c>Uniqueid = listenId</c> ends it: <c>not-answered</c> before ChanSpy started,
///   <c>call-ended</c> after — a listen a stop already ended is not audited again.</item>
/// </list>
/// A sweep ends a <c>dialing</c> listen older than the originate timeout plus 10 s as <c>not-answered</c> (a lost
/// <c>Hangup</c>, or a phone that never confirmed). Natural ends are audited with the system as actor, and only for an
/// accepted listen: the end of one whose originate is still in flight is audited by the service when Asterisk accepts
/// it, and one whose originate failed is never audited (its late leg is hung up through the pending hangup the service
/// arms).
/// <para>
/// It subscribes on first use only (the defer-external-seams rule): a host with no AMI boots, and nothing here runs at
/// host start. AOT-clean: events are matched on the string-keyed <see cref="ManagerEvent.RawFields"/>, as
/// <see cref="AmiDtmfSource"/> does. <c>OnNext</c> never throws.
/// </para>
/// </summary>
internal sealed partial class SupervisorListenObserver : ISupervisorListenLegControl, IObserver<ManagerEvent>, IDisposable
{
    private const string PrimaryNode = "primary";
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DialingGrace = SupervisorListenService.OriginateTimeout + TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TombstoneAge = TimeSpan.FromMinutes(2);

    private readonly VerbaraServerPool _pool;
    private readonly SupervisorListenRegistry _registry;
    private readonly SupervisorListenAudit _audit;
    private readonly IClock _clock;
    private readonly ILogger<SupervisorListenObserver> _logger;
    private readonly Lock _gate = new();
    private IAmiConnection? _observed;
    private IDisposable? _subscription;
    private Timer? _sweepTimer;
    private bool _disposed;

    public SupervisorListenObserver(
        VerbaraServerPool pool,
        SupervisorListenRegistry registry,
        SupervisorListenAudit audit,
        IClock clock,
        ILogger<SupervisorListenObserver> logger)
    {
        _pool = pool;
        _registry = registry;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public void EnsureObserving()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var connection = _pool.GetServer(PrimaryNode)?.Connection;
            if (connection is null || ReferenceEquals(connection, _observed))
                return;

            _subscription?.Dispose();
            _subscription = connection.Subscribe(this);
            _observed = connection;
            _sweepTimer ??= new Timer(static state => _ = ((SupervisorListenObserver)state!).SweepAsync(), this, SweepPeriod, SweepPeriod);
        }
    }

    public async Task HangupAsync(ActiveListen listen, CancellationToken ct)
    {
        var channel = _registry.ChannelOrPendHangup(listen, _clock.UtcNow);
        if (channel is not null)
            await SendHangupAsync(listen.ListenId, channel).ConfigureAwait(false);
    }

    public void OnNext(ManagerEvent value) => _ = HandleSafelyAsync(value);

    public void OnError(Exception error)
    {
        // The AMI connection reconnects on its own; a later EnsureObserving re-subscribes to a new connection.
    }

    public void OnCompleted()
    {
    }

    /// <summary>Handles one AMI event; the testable core of <see cref="OnNext"/>.</summary>
    internal async Task HandleAsync(ManagerEvent evt)
    {
        var fields = evt.RawFields;
        if (fields is null || Field(fields, "Event") is not { } name)
            return;

        if (string.Equals(name, "Newchannel", StringComparison.OrdinalIgnoreCase))
        {
            if (Field(fields, "Uniqueid") is { } id && Field(fields, "Channel") is { } channel && _registry.SetChannel(id, channel))
                await SendHangupAsync(id, channel).ConfigureAwait(false);
        }
        else if (string.Equals(name, "ChanSpyStart", StringComparison.OrdinalIgnoreCase))
        {
            if (Field(fields, "SpyerUniqueid") is { } id && _registry.MarkListening(id, _clock.UtcNow)
                && (Field(fields, "SpyerChannel") ?? _registry.Get(id)?.ChannelName) is { } channel)
                await SendHangupAsync(id, channel).ConfigureAwait(false);
        }
        else if (string.Equals(name, "Hangup", StringComparison.OrdinalIgnoreCase))
        {
            if (Field(fields, "Uniqueid") is not { } id)
                return;
            _registry.ForgetPendingHangup(id);
            var now = _clock.UtcNow;
            if (_registry.TryEnd(id, now) is { } ended)
            {
                LogListenEnded(ended.ListenId, ended.EndReason!);
                // An unaccepted listen (its originate still in flight, or failed) is audited by its acceptor, if any.
                if (ended.Accepted)
                    await _audit.StoppedAsync(ended).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Ends stale <c>dialing</c> listens as <c>not-answered</c> and prunes old bookkeeping.</summary>
    internal async Task SweepAsync()
    {
        try
        {
            var now = _clock.UtcNow;
            var ends = _registry.StaleDialing(now - DialingGrace)
                .Select(stale => _registry.TryEnd(stale.ListenId, now, ListenVocabulary.NotAnswered))
                .OfType<ActiveListen>();
            foreach (var ended in ends)
            {
                LogListenEnded(ended.ListenId, ListenVocabulary.NotAnswered);
                if (ended.ChannelName is { } channel)
                    await SendHangupAsync(ended.ListenId, channel).ConfigureAwait(false);
                if (ended.Accepted)
                    await _audit.StoppedAsync(ended).ConfigureAwait(false);
            }

            _registry.Prune(now - TombstoneAge);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSweepFailed(ex.Message);
        }
    }

    private async Task HandleSafelyAsync(ManagerEvent evt)
    {
        try
        {
            await HandleAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogEventFailed(ex.Message);
        }
    }

    /// <summary>Hangs up the listen's own leg — only that channel, never the agent's call. Best-effort.</summary>
    private async Task SendHangupAsync(string listenId, string channel)
    {
        var server = _pool.GetServer(PrimaryNode);
        if (server is null)
            return;
        try
        {
            await server.Connection.SendActionAsync(new HangupAction { Channel = channel }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHangupFailed(listenId, ex.Message);
        }
    }

    /// <summary>A raw AMI field, by exact name first, then ignoring case (Asterisk spells some ids <c>Uniqueid</c>).</summary>
    private static string? Field(IReadOnlyDictionary<string, string> fields, string name)
    {
        if (fields.TryGetValue(name, out var exact))
            return string.IsNullOrEmpty(exact) ? null : exact;
        foreach (var (key, value) in fields)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _subscription?.Dispose();
            _subscription = null;
            _sweepTimer?.Dispose();
            _sweepTimer = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[LISTEN] Listen {ListenId} ended: {Reason}")]
    private partial void LogListenEnded(string listenId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[LISTEN] Hangup of the leg of listen {ListenId} failed: {Reason}")]
    private partial void LogHangupFailed(string listenId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[LISTEN] An AMI event could not be handled: {Reason}")]
    private partial void LogEventFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[LISTEN] The listen sweep failed: {Reason}")]
    private partial void LogSweepFailed(string reason);
}
