using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Api.Auth;

internal static partial class ApiKeyLastUsedStamperLog
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[API-KEY/LAST-USED] Stamp failed for key={KeyId}: {Reason}")]
    public static partial void StampFailed(ILogger logger, string keyId, string reason);
}

/// <summary>
/// Debounced writer for <see cref="ApiKey.LastUsedAt"/>. Wraps
/// <see cref="IApiKeyStore.UpdateLastUsedAsync"/> with an in-process
/// <see cref="IMemoryCache"/> so a chatty client cannot turn every request
/// into an extra UPDATE — a key is stamped at most <see cref="StampDebounce"/>
/// frequently per process. R5.2 PC.5 / B.12.
/// </summary>
/// <remarks>
/// In-process cache is intentional: a Redis backplane on the auth hot path
/// would introduce a second round-trip per request and a hard dependency on
/// Redis for the auth scheme to work. The trade-off is that, in a clustered
/// deployment, the actual cadence per key is roughly
/// <c>nodes × (1 / StampDebounce)</c> — still bounded, still tiny relative to
/// real-world key-call volume, and never harmful (only the most recent stamp
/// wins on the database).
/// </remarks>
public sealed class ApiKeyLastUsedStamper : IApiKeyLastUsedStamper
{
    /// <summary>Maximum write cadence per key per process. Public for tests.</summary>
    public static readonly TimeSpan StampDebounce = TimeSpan.FromMinutes(1);

    private const string CacheKeyPrefix = "api-key-last-used:";

    private readonly IApiKeyStore _store;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApiKeyLastUsedStamper> _logger;

    public ApiKeyLastUsedStamper(
        IApiKeyStore store,
        IMemoryCache cache,
        TimeProvider timeProvider,
        ILogger<ApiKeyLastUsedStamper> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _cache = cache;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StampAsync(EntityId keyId, CancellationToken ct)
    {
        var cacheKey = CacheKeyPrefix + keyId.Value;
        var now = _timeProvider.GetUtcNow();

        // We intentionally compute the debounce window against TimeProvider
        // rather than the cache's own expiration. MemoryCache evicts on its
        // internal clock which is not test-controllable — checking the
        // cached timestamp keeps the contract verifiable with a fake clock
        // and removes a class of race where two requests arrive ~ at the
        // boundary.
        if (_cache.TryGetValue<DateTimeOffset>(cacheKey, out var lastStamped)
            && now - lastStamped < StampDebounce)
        {
            return; // within debounce window — skip the UPDATE.
        }

        // Cache entry TTL is set to a generous multiple of the debounce window
        // so the entry survives long enough to gate subsequent calls but is
        // bounded for memory hygiene on idle keys.
        _cache.Set(cacheKey, now, StampDebounce + StampDebounce);

        try
        {
            await _store.UpdateLastUsedAsync(keyId, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled — auth still succeeded for this caller, the
            // next request after the debounce window will stamp instead.
        }
        catch (Exception ex)
        {
            // Best-effort: a stamp failure must never break authentication.
            ApiKeyLastUsedStamperLog.StampFailed(_logger, keyId.Value, ex.Message);
        }
    }
}

/// <summary>
/// Fire-and-forget stamper that records when an API key was last used.
/// </summary>
public interface IApiKeyLastUsedStamper
{
    /// <summary>
    /// Best-effort stamp of <see cref="ApiKey.LastUsedAt"/>. Debounced
    /// in-process so repeated calls within
    /// <see cref="ApiKeyLastUsedStamper.StampDebounce"/> are skipped.
    /// </summary>
    Task StampAsync(EntityId keyId, CancellationToken ct);
}
