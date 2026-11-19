using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// Call recordings: where their files are read (<see cref="RecordingOptions"/> from <c>Recordings:BasePath</c>), and the
/// event store's two host seams for a call record: the recording resolver over that directory
/// (call-records-are-written-once design D3), so a call record names a recording only when the call's file exists, and
/// the agent resolver (call-records-name-the-queue-agent design D2), so an answered queue call's record names the
/// Platform agent whose member interface took it.
/// </summary>
public static class RecordingsExtensions
{
    /// <summary>
    /// Binds <see cref="RecordingOptions.BasePath"/> from <c>Recordings:BasePath</c> (empty keeps the default) and
    /// registers <see cref="ICallRecordingResolver"/> and <see cref="ICallAgentResolver"/>; the event store resolves each
    /// at its first projection that needs it, never at host start, and a host without the event store never resolves
    /// them.
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
        services.TryAddSingleton<ICallAgentResolver, AgentInterfaceCallAgentResolver>();
        return services;
    }
}
