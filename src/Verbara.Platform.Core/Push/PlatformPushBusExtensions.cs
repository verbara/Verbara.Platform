using System.Globalization;

using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Core.Push;

/// <summary>
/// Registers the in-process push bus of a Platform host (Api, Realtime) with a buffer sized for the 250-concurrent-call
/// target instead of the Sdk's library default of 256 events.
/// </summary>
/// <remarks>
/// <para>
/// The bus hands events to its observers from one dispatch loop. Its buffer is the time that loop may fall behind
/// before the oldest events are evicted (<see cref="BackpressureStrategy.DropOldest"/>, counted as
/// <c>asterisk.push.events.dropped{reason="buffer_full"}</c>): capacity divided by the event rate. At 250 calls a
/// replica's bus carries about 250 events a second, so 256 events last about one second, and a replica that is busy
/// for longer, for example while many agents sign in at once, loses live events (transcripts, sentiment) for good.
/// </para>
/// <para>
/// The default, <see cref="DefaultBufferCapacity"/>, keeps about a minute at that rate and half a minute at twice the
/// target, for at most about 16 MB of queued events per replica. The buffer stays bounded: a replica that stalls for longer still
/// evicts, and counts, the oldest events rather than growing without limit. A host that carries more traffic sets
/// <see cref="BufferCapacityKey"/> (environment variable <c>Push__Bus__BufferCapacity</c>).
/// </para>
/// </remarks>
public static class PlatformPushBusExtensions
{
    /// <summary>The configuration key of the bus's buffer, in events.</summary>
    public const string BufferCapacityKey = "Push:Bus:BufferCapacity";

    /// <summary>The bus's buffer when <see cref="BufferCapacityKey"/> is absent or empty, in events.</summary>
    public const int DefaultBufferCapacity = 16_384;

    /// <summary>
    /// Registers the push bus (<c>AddVerbaraPush</c>) with its buffer read from <see cref="BufferCapacityKey"/>, or
    /// <see cref="DefaultBufferCapacity"/>. A value that is not an integer fails when the options are first read, naming
    /// the key; a value below 1 fails the Sdk's validation.
    /// </summary>
    public static IServiceCollection AddPlatformPushBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddVerbaraPush();
        services.AddOptions<PushEventBusOptions>()
            .Configure<IServiceProvider>(static (options, provider) =>
                options.BufferCapacity = ReadBufferCapacity(provider.GetService<IConfiguration>()));
        return services;
    }

    private static int ReadBufferCapacity(IConfiguration? configuration)
    {
        var raw = configuration?[BufferCapacityKey];
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultBufferCapacity;

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
            return capacity;

        throw new InvalidOperationException(
            $"{BufferCapacityKey} must be an integer number of events (was '{raw}').");
    }
}
