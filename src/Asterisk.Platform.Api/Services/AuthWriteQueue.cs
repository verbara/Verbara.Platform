using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// AHH Phase 2 — bounded background queue for the success-side login writes
/// (<c>users.last_login_at</c> updates, lockout-counter resets, success
/// <c>auth_events</c> inserts). Removes 5–10 ms × 3 sync DB ops from the
/// request critical path on every <c>POST /auth/login</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capacity</b>: bounded at 4 096 items with
/// <see cref="BoundedChannelFullMode.DropWrite"/>. Drops are reported via the
/// <c>auth.write.dropped</c> counter; under DoS-style sustained &gt;10×
/// knee load some success-side writes are intentionally lost. Audit failures
/// never use this queue (see ADR-0011 §"Failure-path invariant").
/// </para>
/// <para>
/// <b>Consumer</b>: a single background reader drains up to 64 items per
/// flush, coalescing user-mutating commands by <c>(tenantId, userId)</c> so
/// at most one <c>users</c> upsert is issued per user per batch.
/// <c>auth_events</c> inserts are independent rows and processed
/// individually.
/// </para>
/// <para>
/// <b>Meter</b>: <c>Asterisk.Platform.Auth.WriteQueue</c> exposes
/// <c>auth.write.{enqueued,dropped,processed,failed}</c> with a <c>type</c>
/// dimension so Grafana can split by command kind.
/// </para>
/// </remarks>
internal sealed partial class AuthWriteQueue : BackgroundService
{
    /// <summary>Public meter name (consumed by the OpenTelemetry registration).</summary>
    public const string MeterName = "Asterisk.Platform.Auth.WriteQueue";

    private const int DefaultCapacity = 4096;
    private const int DefaultBatchSize = 64;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<AuthWriteCommand> _channel;
    private readonly IServiceProvider _services;
    private readonly ILogger<AuthWriteQueue> _logger;
    private readonly TimeSpan _flushInterval;
    private readonly int _batchSize;

    private readonly Meter _meter;
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _failed;

    public AuthWriteQueue(
        IServiceProvider services,
        ILogger<AuthWriteQueue> logger,
        int? capacity = null,
        int? batchSize = null,
        TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
        _batchSize = batchSize ?? DefaultBatchSize;
        _flushInterval = flushInterval ?? DefaultFlushInterval;

        // BoundedChannelFullMode.Wait makes TryWrite return false synchronously
        // when the channel is full (without blocking). DropWrite would silently
        // accept the call and discard the item — we want the producer to see
        // the drop so we can increment the meter + log a warning.
        _channel = Channel.CreateBounded<AuthWriteCommand>(new BoundedChannelOptions(capacity ?? DefaultCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _meter = new Meter(MeterName);
        _enqueued = _meter.CreateCounter<long>("auth.write.enqueued",
            description: "Items successfully enqueued for deferred write.");
        _dropped = _meter.CreateCounter<long>("auth.write.dropped",
            description: "Items rejected because the bounded channel was full.");
        _processed = _meter.CreateCounter<long>("auth.write.processed",
            description: "Items successfully persisted by the consumer.");
        _failed = _meter.CreateCounter<long>("auth.write.failed",
            description: "Items the consumer could not persist (logged + counted, NOT retried).");
    }

    /// <summary>
    /// Best-effort enqueue. Returns <c>false</c> when the channel is full
    /// (the <c>auth.write.dropped</c> counter is incremented). Callers MUST
    /// NOT use this method for failure-path audit logging — that path stays
    /// synchronous via <see cref="AuthEventService.LogAsync"/>.
    /// </summary>
    public bool TryEnqueue(AuthWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tag = new KeyValuePair<string, object?>("type", command.TypeName);
        if (_channel.Writer.TryWrite(command))
        {
            _enqueued.Add(1, tag);
            return true;
        }

        _dropped.Add(1, tag);
        LogQueueFull(_logger, command.TypeName);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var batch = new List<AuthWriteCommand>(_batchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                while (batch.Count < _batchSize && reader.TryRead(out var cmd))
                    batch.Add(cmd);

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                    batch.Clear();
                }

                // Inter-batch pause lets more commands accumulate so we can
                // coalesce same-user mutations into a single DB upsert.
                if (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_flushInterval, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* shutdown */ }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }

        // Drain the channel on graceful shutdown so in-flight enqueues are
        // flushed instead of silently dropped.
        while (reader.TryRead(out var cmd))
            batch.Add(cmd);
        if (batch.Count > 0)
        {
            try
            {
                await ProcessBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogShutdownDrainFailed(_logger, ex, batch.Count);
            }
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<AuthWriteCommand> batch, CancellationToken ct)
    {
        // Fresh DI scope per batch so transient/scoped store dependencies (if
        // any are introduced later) get correct lifetime semantics.
        using var scope = _services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var authEventStore = scope.ServiceProvider.GetRequiredService<IAuthEventStore>();

        // Coalesce user-mutating commands by user. Last-writer-wins for the
        // LastLoginAt timestamp; ResetLockoutCounters is idempotent.
        var userMutations = new Dictionary<(string TenantId, string UserId), UserMutation>();
        var logEvents = new List<LogSuccessEventCommand>();

        foreach (var cmd in batch)
        {
            switch (cmd)
            {
                case UpdateLastLoginAtCommand u:
                {
                    var key = (u.TenantId, u.UserId);
                    var existing = userMutations.TryGetValue(key, out var v) ? v : default;
                    existing.LastLoginAt = u.At;
                    existing.HasLastLogin = true;
                    userMutations[key] = existing;
                    break;
                }
                case ResetLockoutCountersCommand r:
                {
                    var key = (r.TenantId, r.UserId);
                    var existing = userMutations.TryGetValue(key, out var v) ? v : default;
                    existing.ResetLockout = true;
                    userMutations[key] = existing;
                    break;
                }
                case LogSuccessEventCommand l:
                    logEvents.Add(l);
                    break;
            }
        }

        // Apply user mutations (one read + one upsert per user per batch).
        foreach (var (key, mutation) in userMutations)
        {
            try
            {
                var user = await userStore.GetByIdAsync(
                    new TenantId(key.TenantId),
                    EntityId.From(key.UserId),
                    ct).ConfigureAwait(false);
                if (user is null)
                {
                    if (mutation.HasLastLogin)
                        _failed.Add(1, new KeyValuePair<string, object?>("type", "update_last_login_at"));
                    if (mutation.ResetLockout)
                        _failed.Add(1, new KeyValuePair<string, object?>("type", "reset_lockout_counters"));
                    continue;
                }

                if (mutation.HasLastLogin)
                    user.LastLoginAt = mutation.LastLoginAt;
                if (mutation.ResetLockout)
                {
                    user.FailedLoginAttempts = 0;
                    user.LockedUntil = null;
                }

                await userStore.SaveAsync(user, ct).ConfigureAwait(false);

                if (mutation.HasLastLogin)
                    _processed.Add(1, new KeyValuePair<string, object?>("type", "update_last_login_at"));
                if (mutation.ResetLockout)
                    _processed.Add(1, new KeyValuePair<string, object?>("type", "reset_lockout_counters"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (mutation.HasLastLogin)
                    _failed.Add(1, new KeyValuePair<string, object?>("type", "update_last_login_at"));
                if (mutation.ResetLockout)
                    _failed.Add(1, new KeyValuePair<string, object?>("type", "reset_lockout_counters"));
                LogUserMutationFailed(_logger, ex, key.TenantId, key.UserId);
            }
        }

        // Apply log events independently — each is its own auth_events row.
        foreach (var le in logEvents)
        {
            try
            {
                var authEvent = new AuthEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    TenantId = le.TenantId,
                    UserId = le.UserId,
                    EventType = le.EventType,
                    IpAddress = le.IpAddress,
                    UserAgent = le.UserAgent,
                    Details = null,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                await authEventStore.SaveAsync(authEvent, ct).ConfigureAwait(false);
                _processed.Add(1, new KeyValuePair<string, object?>("type", "log_success_event"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failed.Add(1, new KeyValuePair<string, object?>("type", "log_success_event"));
                LogAuthEventFailed(_logger, ex, le.EventType);
            }
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }

    private struct UserMutation
    {
        public DateTimeOffset LastLoginAt;
        public bool HasLastLogin;
        public bool ResetLockout;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "AuthWriteQueue is full; dropped command of type {CommandType}.")]
    private static partial void LogQueueFull(ILogger logger, string commandType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "AuthWriteQueue user mutation failed for tenant {TenantId} user {UserId}.")]
    private static partial void LogUserMutationFailed(ILogger logger, Exception ex, string tenantId, string userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "AuthWriteQueue auth_events insert failed for event {EventType}.")]
    private static partial void LogAuthEventFailed(ILogger logger, Exception ex, string eventType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "AuthWriteQueue shutdown drain failed; {Count} commands lost.")]
    private static partial void LogShutdownDrainFailed(ILogger logger, Exception ex, int count);
}
