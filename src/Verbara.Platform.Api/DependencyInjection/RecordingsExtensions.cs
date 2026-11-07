using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// Call recordings: where their files are read (<see cref="RecordingOptions"/> from <c>Recordings:BasePath</c>), and the
/// event store's recording resolver over that directory (call-records-are-written-once design D3), so a call record
/// names a recording only when the call's file exists.
/// </summary>
public static class RecordingsExtensions
{
    /// <summary>
    /// Binds <see cref="RecordingOptions.BasePath"/> from <c>Recordings:BasePath</c> (empty keeps the default) and
    /// registers <see cref="ICallRecordingResolver"/>; the event store resolves it at its first projection, never at host
    /// start, and a host without the event store never resolves it.
    /// </summary>
    public static IServiceCollection AddPlatformRecordings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RecordingOptions>(o =>
        {
            var path = configuration["Recordings:BasePath"];
            if (!string.IsNullOrEmpty(path))
                o.BasePath = path;
        });
        services.TryAddSingleton<ICallRecordingResolver, RecordingFileCallRecordingResolver>();
        return services;
    }
}
