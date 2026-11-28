using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Storage.Postgres;

namespace Verbara.Platform.Api.Services;

internal static partial class DatabaseMigrationService
{
    /// <summary>
    /// Synchronously apply all pending Platform SQL migrations.
    /// Called early in Program.cs before Pro packages register
    /// (their EnsureSchemaAsync references Platform tables like contacts).
    /// </summary>
    /// <returns>
    /// Every notice the applied migrations raised, in order (#701). The host's logging is not built yet when this
    /// runs, so Program.cs writes them with <see cref="LogNotices(IServiceProvider, IReadOnlyList{MigrationNotice})"/>
    /// right after <c>builder.Build()</c>. The runner itself is <see cref="PlatformMigrationRunner"/>, which the CLI's
    /// <c>migrate</c> command uses too (#711).
    /// </returns>
    public static IReadOnlyList<MigrationNotice> ApplyMigrations(string connectionString) =>
        PlatformMigrationRunner.Apply(connectionString);

    /// <summary>Applies <paramref name="migrations"/> (name, SQL) in ordinal name order; the seam the tests drive.</summary>
    internal static IReadOnlyList<MigrationNotice> ApplyMigrations(
        string connectionString, IEnumerable<(string Name, string Sql)> migrations) =>
        PlatformMigrationRunner.Apply(connectionString, migrations);

    /// <summary>Writes every notice to the host log under this runner's category (#701).</summary>
    public static void LogNotices(IServiceProvider services, IReadOnlyList<MigrationNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(services);
        LogNotices(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrationService).FullName!),
            notices);
    }

    /// <summary>
    /// Writes each notice with its migration's name: at Warning when Postgres raised it as a <c>WARNING</c>, at
    /// Information otherwise (<c>NOTICE</c>, <c>INFO</c>, <c>LOG</c>, <c>DEBUG</c>).
    /// </summary>
    public static void LogNotices(ILogger logger, IReadOnlyList<MigrationNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(notices);
        foreach (var notice in notices)
        {
            if (notice.IsWarning)
                LogWarningNotice(logger, notice.Migration, notice.Text);
            else
                LogNotice(logger, notice.Migration, notice.Severity, notice.Text);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Migration {Migration} reported ({Severity}): {Notice}")]
    private static partial void LogNotice(ILogger logger, string migration, string severity, string notice);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Migration {Migration} reported (WARNING): {Notice}")]
    private static partial void LogWarningNotice(ILogger logger, string migration, string notice);
}
