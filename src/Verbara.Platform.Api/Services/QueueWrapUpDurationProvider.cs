using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Core;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Services;

internal static partial class QueueWrapUpDurationProviderLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[QUEUE-WRAPUP] Queue {QueueName} is not a Platform queue; its wrap-up uses the session fallback")]
    public static partial void UnknownQueue(ILogger logger, string queueName);
}

/// <summary>
/// Answers the session engine with each queue's configured wrap-up, by the name Platform wrote to Asterisk. The
/// engine asks on the thread that delivers that server's AMI events, so the answer comes from a snapshot held in
/// memory: this class reads no store and takes no lock. A refresh pass swaps a whole snapshot in, and a save
/// publishes a new one with its own key changed.
/// </summary>
internal sealed class QueueWrapUpDurationProvider : IWrapUpDurationProvider
{
    /// <summary>How two queue names are compared: Asterisk can report a name in another case.</summary>
    public static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ILogger<QueueWrapUpDurationProvider> _logger;
    private readonly ConcurrentDictionary<string, byte> _warnedNames = new(KeyComparer);

    private volatile Snapshot _snapshot = new(ImmutableDictionary.Create<string, TimeSpan>(KeyComparer), Loaded: false);

    public QueueWrapUpDurationProvider(ILogger<QueueWrapUpDurationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The queue's name as Asterisk knows it. It is built here and never parsed back, because a tenant id may
    /// contain the separator.
    /// </summary>
    public static string KeyOf(TenantId tenantId, string queueName) => $"{tenantId.Value}-{queueName}";

    public TimeSpan? GetWrapUpDuration(string serverId, string queueName)
    {
        var snapshot = _snapshot;
        if (snapshot.Values.TryGetValue(queueName, out var duration))
        {
            return duration;
        }

        if (snapshot.Loaded && _warnedNames.TryAdd(queueName, 0))
        {
            QueueWrapUpDurationProviderLog.UnknownQueue(_logger, queueName);
        }

        return null;
    }

    /// <summary>
    /// Changes one answer, keyed by <see cref="KeyOf"/>, and leaves every other one alone. A pass that read the store
    /// before this call may still replace it; the next pass applies the saved value.
    /// </summary>
    public void Update(string key, TimeSpan value)
    {
        ArgumentNullException.ThrowIfNull(key);

        var seen = _snapshot;
        while (true)
        {
            var next = seen with { Values = seen.Values.SetItem(key, value) };
            var current = Interlocked.CompareExchange(ref _snapshot, next, seen);
            if (ReferenceEquals(current, seen))
            {
                return;
            }

            seen = current;
        }
    }

    /// <summary>
    /// Replaces every answer at once, keyed by <see cref="KeyOf"/>. A key in <paramref name="reportedKeys"/> is a
    /// Platform queue whose caller already reported why it has no answer, so it is never reported here as unknown.
    /// </summary>
    public void Replace(IReadOnlyDictionary<string, TimeSpan> values, IEnumerable<string>? reportedKeys = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var key in reportedKeys ?? [])
        {
            _warnedNames.TryAdd(key, 0);
        }

        _snapshot = new Snapshot(ImmutableDictionary.CreateRange(KeyComparer, values), Loaded: true);
    }

    // Loaded stays false until a whole pass has run: before it a queue is merely not loaded yet, so none is reported
    // as unknown, whatever a save already wrote.
    private sealed record Snapshot(ImmutableDictionary<string, TimeSpan> Values, bool Loaded);
}
