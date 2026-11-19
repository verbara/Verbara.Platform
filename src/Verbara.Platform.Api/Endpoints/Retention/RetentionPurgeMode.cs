using Verbara.Sdk.Pro.Storage.Common.Retention;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Endpoints.Retention;

/// <summary>
/// Read-only view of the mode the scheduled Pro retention purge of this process runs with.
/// </summary>
/// <remarks>
/// The mode is deployment configuration (<see cref="RetentionPurgeModeConfiguration.Key"/>), fixed for the life of
/// the process: changing it takes a configuration change and a restart. Nothing in the management API changes it.
/// <c>GET /management/retention/config</c> reports this value, and in-process consumers that must know whether
/// expired rows are really deleted read it here.
/// </remarks>
public interface IRetentionPurgeMode
{
    /// <summary><see langword="true"/> when the scheduled purge only counts expired rows.</summary>
    bool ScheduledDryRun { get; }
}

/// <summary>
/// <see cref="IRetentionPurgeMode"/> over <see cref="IOptions{RetentionOptions}"/>: the same options instance the Pro
/// retention service captures at construction, so the two cannot disagree inside one process.
/// </summary>
public sealed class RetentionPurgeMode : IRetentionPurgeMode
{
    public RetentionPurgeMode(IOptions<RetentionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ScheduledDryRun = options.Value.DryRun;
    }

    /// <inheritdoc />
    public bool ScheduledDryRun { get; }
}

/// <summary>Source-generated startup log line that states the scheduled purge mode.</summary>
public static partial class RetentionPurgeModeLog
{
    /// <summary>Logs, once at startup, the mode of the host's <see cref="IRetentionPurgeMode"/>.</summary>
    public static void LogStartupMode(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        LogStartupMode(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RetentionPurgeMode).FullName!),
            services.GetRequiredService<IRetentionPurgeMode>().ScheduledDryRun);
    }

    /// <summary>Logs the mode once: Information when dry, Warning when the purge deletes.</summary>
    public static void LogStartupMode(ILogger logger, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (dryRun)
            LogDryRun(logger);
        else
            LogLive(logger);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Pro retention purge runs in dry-run mode — set Retention:DryRun=false to delete expired rows.")]
    private static partial void LogDryRun(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Pro retention purge deletes expired rows (Retention:DryRun=false).")]
    private static partial void LogLive(ILogger logger);
}
