using Asterisk.Platform.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Asterisk.Platform.Identity.Redis;

/// <summary>
/// AHH Phase 1 — cross-replica invalidation broadcaster for the auth hot-path
/// caches. Subscribes to the Redis pubsub channel
/// <see cref="AuthHotpathCacheKeys.PubSubChannel"/> and dispatches incoming
/// messages to local <see cref="ILocalAuthCacheInvalidationSink"/> handlers
/// (one per cache decorator). Also exposes <c>PublishTenantAuthAsync</c> /
/// <c>PublishUserAsync</c> / <c>PublishPermissionsAsync</c> so each replica
/// can broadcast its own write-side invalidations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> A single line of UTF-8 with pipe-delimited fields:
/// <c>type|tenantId|arg1|arg2</c>. Field count + meaning vary by type:
/// <list type="bullet">
///   <item><description><c>tenant-auth|{tenantId}</c> — invalidate <see cref="ITenantAuthConfigStore"/> entry.</description></item>
///   <item><description><c>user|{tenantId}|{userId}|{email}</c> — invalidate <see cref="IUserStore"/> entries (email may be empty).</description></item>
///   <item><description><c>permissions|{tenantId}|{userId}</c> — invalidate <c>PermissionResolver</c> entry.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Trust boundary.</b> Messages carry <i>only keys</i>, never values. The
/// pubsub channel never transports password hashes, MFA secrets, recovery
/// codes, or any other sensitive material. See ADR-0010.
/// </para>
/// <para>
/// <b>Ordering / loops.</b> Each invalidator instance ignores messages it
/// originated itself (matched by an instance id prefix) so the originator's
/// local cache is not double-invalidated.
/// </para>
/// </remarks>
public interface ILocalAuthCacheInvalidationSink
{
    /// <summary>Invalidate a tenant-auth cache entry.</summary>
    void InvalidateTenantAuth(string tenantId);

    /// <summary>Invalidate a user-by-id and (when known) user-by-email cache entry.</summary>
    void InvalidateUser(string tenantId, string userId, string? email);

    /// <summary>Invalidate a per-user permission set in <c>PermissionResolver</c>.</summary>
    void InvalidatePermissions(string tenantId, string userId);
}

/// <summary>
/// Publish-only side of the auth-cache invalidation pubsub. The cache
/// decorators (<c>CachedUserStore</c>, <c>CachedTenantAuthConfigStore</c>,
/// <c>PermissionResolver</c>) depend on this interface — never on the
/// concrete <see cref="RedisAuthCacheInvalidator"/> — so DI singleton
/// resolution doesn't form a cycle through the
/// <see cref="ILocalAuthCacheInvalidationSink"/> sink registrations
/// (which themselves resolve the decorators). v1.14.2 fix.
/// </summary>
public interface IAuthCachePublisher
{
    /// <summary>Broadcast a tenant-auth invalidation across the cluster.</summary>
    Task PublishTenantAuthAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Broadcast a user-by-id / user-by-email invalidation across the cluster.</summary>
    Task PublishUserAsync(string tenantId, string userId, string? email, CancellationToken ct = default);

    /// <summary>Broadcast a per-user permission-set invalidation across the cluster.</summary>
    Task PublishPermissionsAsync(string tenantId, string userId, CancellationToken ct = default);
}

/// <summary>
/// v1.14.2 — publish-only side of auth cache invalidation. Holds NO
/// dependency on the sink set, so the cache decorators (which ARE the
/// sinks) can take a constructor dep on this without forming a singleton
/// resolution cycle. Pre-v1.14.2, the decorators depended on the
/// concrete <see cref="RedisAuthCacheInvalidator"/> which itself takes
/// <c>IEnumerable&lt;ILocalAuthCacheInvalidationSink&gt;</c> in its
/// constructor → singleton mutex deadlock at startup.
/// </summary>
public sealed class RedisAuthCachePublisher : IAuthCachePublisher
{
    private const string TypeTenantAuth = "tenant-auth";
    private const string TypeUser = "user";
    private const string TypePermissions = "permissions";

    private readonly IConnectionMultiplexer _redis;
    private readonly string _instanceId;

    /// <summary>The originator id that prefixes every publish payload.</summary>
    public string InstanceId => _instanceId;

    public RedisAuthCachePublisher(IConnectionMultiplexer redis)
        : this(redis, Guid.NewGuid().ToString("N"))
    {
    }

    /// <summary>Test-friendly constructor allowing a deterministic instance id.</summary>
    internal RedisAuthCachePublisher(IConnectionMultiplexer redis, string instanceId)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(instanceId);
        _redis = redis;
        _instanceId = instanceId;
    }

    public Task PublishTenantAuthAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var payload = $"{_instanceId}|{TypeTenantAuth}|{tenantId}";
        return PublishRawAsync(payload, ct);
    }

    public Task PublishUserAsync(string tenantId, string userId, string? email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        var payload = $"{_instanceId}|{TypeUser}|{tenantId}|{userId}|{email ?? string.Empty}";
        return PublishRawAsync(payload, ct);
    }

    public Task PublishPermissionsAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        var payload = $"{_instanceId}|{TypePermissions}|{tenantId}|{userId}";
        return PublishRawAsync(payload, ct);
    }

    private async Task PublishRawAsync(string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var subscriber = _redis.GetSubscriber();
        _ = await subscriber.PublishAsync(
            RedisChannel.Literal(AuthHotpathCacheKeys.PubSubChannel),
            payload).ConfigureAwait(false);
    }
}

/// <summary>
/// AHH Phase 1 hosted service that bridges Redis pubsub invalidation messages
/// to local <see cref="ILocalAuthCacheInvalidationSink"/> sinks.
/// </summary>
/// <remarks>
/// v1.14.2 split: the publish-side surface lives on
/// <see cref="RedisAuthCachePublisher"/> so decorators can depend on
/// <see cref="IAuthCachePublisher"/> WITHOUT pulling the sink IEnumerable
/// resolution into their constructor graph. This class is the
/// receive-side: subscribes to Redis pubsub + dispatches incoming
/// messages to the registered sinks. It also implements
/// <see cref="IAuthCachePublisher"/> for backward compatibility +
/// internal use, but the recommended publisher dependency is
/// <see cref="RedisAuthCachePublisher"/>.
/// </remarks>
public sealed partial class RedisAuthCacheInvalidator : IHostedService, IAuthCachePublisher, IAsyncDisposable
{
    private const string TypeTenantAuth = "tenant-auth";
    private const string TypeUser = "user";
    private const string TypePermissions = "permissions";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILocalAuthCacheInvalidationSink[] _sinks;
    private readonly ILogger<RedisAuthCacheInvalidator> _logger;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    private ChannelMessageQueue? _queue;

    /// <summary>
    /// The unique-per-process originator id that prefixes every published
    /// invalidation message. Exposed for unit tests (verifying self-suppression).
    /// </summary>
    internal string InstanceId => _instanceId;

    public RedisAuthCacheInvalidator(
        IConnectionMultiplexer redis,
        IEnumerable<ILocalAuthCacheInvalidationSink> sinks,
        ILogger<RedisAuthCacheInvalidator> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _sinks = sinks.ToArray();
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscriber = _redis.GetSubscriber();
        _queue = await subscriber.SubscribeAsync(
            RedisChannel.Literal(AuthHotpathCacheKeys.PubSubChannel)).ConfigureAwait(false);
        _queue.OnMessage(channelMessage => OnRedisMessage(channelMessage.Message));
        LogStarted(_logger, AuthHotpathCacheKeys.PubSubChannel, _sinks.Length);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_queue is not null)
        {
            await _queue.UnsubscribeAsync().ConfigureAwait(false);
            _queue = null;
        }
        LogStopped(_logger);
    }

    /// <summary>Publish a tenant-auth invalidation across the cluster.</summary>
    public Task PublishTenantAuthAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var payload = $"{_instanceId}|{TypeTenantAuth}|{tenantId}";
        return PublishRawAsync(payload, ct);
    }

    /// <summary>Publish a user invalidation across the cluster.</summary>
    public Task PublishUserAsync(string tenantId, string userId, string? email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        var payload = $"{_instanceId}|{TypeUser}|{tenantId}|{userId}|{email ?? string.Empty}";
        return PublishRawAsync(payload, ct);
    }

    /// <summary>Publish a permissions invalidation across the cluster.</summary>
    public Task PublishPermissionsAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(userId);
        var payload = $"{_instanceId}|{TypePermissions}|{tenantId}|{userId}";
        return PublishRawAsync(payload, ct);
    }

    private async Task PublishRawAsync(string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var subscriber = _redis.GetSubscriber();
        // PublishAsync returns Task<long> (subscriber count); we don't expose
        // it to callers — the count is useful for diagnostics, not for the
        // invalidation API surface.
        _ = await subscriber.PublishAsync(
            RedisChannel.Literal(AuthHotpathCacheKeys.PubSubChannel),
            payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch a single Redis pubsub message to the registered sinks.
    /// Internal-only so unit tests can exercise the wire-format parser without
    /// standing up a real Redis fixture.
    /// </summary>
    internal void OnRedisMessage(RedisValue message)
    {
        var raw = (string?)message;
        if (string.IsNullOrEmpty(raw))
            return;

        var parts = raw.Split('|');
        if (parts.Length < 2)
            return;

        var originator = parts[0];
        if (originator == _instanceId)
            return; // suppress our own publishes

        var type = parts[1];
        try
        {
            switch (type)
            {
                case TypeTenantAuth when parts.Length >= 3:
                    foreach (var sink in _sinks)
                        sink.InvalidateTenantAuth(parts[2]);
                    break;
                case TypeUser when parts.Length >= 4:
                    var email = parts.Length >= 5 ? (parts[4].Length == 0 ? null : parts[4]) : null;
                    foreach (var sink in _sinks)
                        sink.InvalidateUser(parts[2], parts[3], email);
                    break;
                case TypePermissions when parts.Length >= 4:
                    foreach (var sink in _sinks)
                        sink.InvalidatePermissions(parts[2], parts[3]);
                    break;
                default:
                    LogUnknownType(_logger, type);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDispatchFailed(_logger, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_queue is not null)
        {
            await _queue.UnsubscribeAsync().ConfigureAwait(false);
            _queue = null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "RedisAuthCacheInvalidator started on channel '{Channel}' with {SinkCount} local sink(s).")]
    private static partial void LogStarted(ILogger logger, string channel, int sinkCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "RedisAuthCacheInvalidator stopped.")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "RedisAuthCacheInvalidator received unknown message type '{Type}'; ignored.")]
    private static partial void LogUnknownType(ILogger logger, string type);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "RedisAuthCacheInvalidator failed to dispatch a message; ignored.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception ex);
}
