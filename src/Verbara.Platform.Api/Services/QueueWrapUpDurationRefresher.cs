using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

internal static partial class QueueWrapUpDurationRefresherLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[QUEUE-WRAPUP] Queues named {QueueKey} have different wrap-ups; that name uses the session fallback")]
    public static partial void ConflictingKey(ILogger logger, string queueKey);
}

/// <summary>
/// Loads every queue of every active tenant into <see cref="QueueWrapUpDurationProvider"/>. One pass builds a
/// complete map and swaps it in only when the whole pass succeeded, so a store failure propagates to the caller and
/// the last answers stay whole. An inactive queue is loaded too: a call that entered before the change can still
/// complete from it.
/// </summary>
internal sealed class QueueWrapUpDurationRefresher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly QueueWrapUpDurationProvider _provider;
    private readonly ILogger<QueueWrapUpDurationRefresher> _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedKeys = new(QueueWrapUpDurationProvider.KeyComparer);

    public QueueWrapUpDurationRefresher(
        IServiceScopeFactory scopes,
        QueueWrapUpDurationProvider provider,
        ILogger<QueueWrapUpDurationRefresher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _provider = provider;
        _logger = logger;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var queueStore = scope.ServiceProvider.GetRequiredService<IQueueStore>();

        var values = new Dictionary<string, TimeSpan>(QueueWrapUpDurationProvider.KeyComparer);
        var conflicting = new HashSet<string>(QueueWrapUpDurationProvider.KeyComparer);

        var tenants = await tenantStore.GetAllActiveAsync(ct).ConfigureAwait(false);
        foreach (var tenant in tenants)
        {
            var tenantId = new TenantId(tenant.TenantId);
            var queues = await queueStore.ListAllAsync(tenantId, ct).ConfigureAwait(false);
            foreach (var queue in queues)
            {
                Add(QueueWrapUpDurationProvider.KeyOf(tenantId, queue.Name), queue, values, conflicting);
            }
        }

        foreach (var key in conflicting)
        {
            if (_warnedKeys.TryAdd(key, 0))
            {
                QueueWrapUpDurationRefresherLog.ConflictingKey(_logger, key);
            }
        }

        _provider.Replace(values, conflicting);
    }

    // Queue names are not unique and keep their casing, so two queues can share a key. When their values differ the
    // key answers nothing, whatever order they were listed in, and the engine uses its fallback.
    private static void Add(
        string key, Queue queue, Dictionary<string, TimeSpan> values, HashSet<string> conflicting)
    {
        if (conflicting.Contains(key))
        {
            return;
        }

        var value = TimeSpan.FromSeconds(queue.WrapUp.DefaultWrapUpSeconds);
        if (values.TryGetValue(key, out var seen) && seen != value)
        {
            values.Remove(key);
            conflicting.Add(key);
            return;
        }

        values[key] = value;
    }
}
