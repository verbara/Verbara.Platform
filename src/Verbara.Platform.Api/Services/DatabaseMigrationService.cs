using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Verbara.Platform.Api.Services;

/// <summary>A notice a migration raised while the runner applied it (<c>RAISE NOTICE</c>, <c>RAISE WARNING</c>, ...).</summary>
/// <param name="Migration">The migration file the notice came from, e.g. <c>035_SurveyResponseOnePerDispatch.sql</c>.</param>
/// <param name="Severity">The Postgres severity, untranslated (<c>NOTICE</c>, <c>WARNING</c>, <c>INFO</c>, ...).</param>
/// <param name="Text">The notice text.</param>
internal sealed record MigrationNotice(string Migration, string Severity, string Text);

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
    /// right after <c>builder.Build()</c>.
    /// </returns>
    public static IReadOnlyList<MigrationNotice> ApplyMigrations(string connectionString) =>
        ApplyMigrations(connectionString, GetMigrationFiles());

    /// <summary>Applies <paramref name="migrations"/> (name, SQL) in ordinal name order; the seam the tests drive.</summary>
    internal static IReadOnlyList<MigrationNotice> ApplyMigrations(
        string connectionString, IEnumerable<(string Name, string Sql)> migrations)
    {
        // `using`, so the source is disposed on every exit, a failed connection or migration included:
        // an undisposed data source keeps reporting its pool on /metrics, labelled with its connection
        // string, for the life of the process (measure-api-multi-replica-baseline D2).
        using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var conn = dataSource.OpenConnection();

        // Notices arrive on the connection while a command runs; only those raised by a migration are kept
        // (the runner's own bookkeeping raises "relation _migrations already exists, skipping" on every start).
        var notices = new List<MigrationNotice>();
        string? current = null;
        conn.Notice += (_, e) =>
        {
            if (current is not null)
                notices.Add(new MigrationNotice(current, e.Notice.InvariantSeverity, e.Notice.MessageText));
        };

        ExecuteNonQuery(conn, null,
            "CREATE TABLE IF NOT EXISTS _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");

        var applied = QueryMigrationNames(conn).ToHashSet(StringComparer.Ordinal);

        foreach (var migration in migrations
                     .Where(m => !applied.Contains(m.Name))
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            using var tx = conn.BeginTransaction();
            var raisedBefore = notices.Count;
            try
            {
                current = migration.Name;
                ExecuteNonQuery(conn, tx, migration.Sql);
                current = null;
                ExecuteNonQuery(conn, tx,
                    "INSERT INTO _migrations (name) VALUES (@Name)",
                    new NpgsqlParameter("Name", migration.Name));
                tx.Commit();
            }
            catch (Exception ex)
            {
                current = null;
                // Postgres aborts the transaction server-side on SQL error;
                // the client-side Rollback() then throws "transaction has completed".
                // Swallow that secondary exception so the real migration error surfaces.
                try { tx.Rollback(); }
                catch (InvalidOperationException) { /* tx already aborted by server */ }
                // The host stops on this exception before its logging exists, so the notices of the migrations this
                // run committed before the failure travel in its message: they are not raised again on the next start.
                throw new InvalidOperationException(
                    $"Migration '{migration.Name}' failed: {ex.Message}{DescribeCommitted(notices, raisedBefore)}", ex);
            }
        }

        return notices;
    }

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
            if (string.Equals(notice.Severity, "WARNING", StringComparison.Ordinal))
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

    private static string DescribeCommitted(List<MigrationNotice> notices, int committedCount)
    {
        if (committedCount == 0)
            return string.Empty;
        return " Notices of the migrations applied before it: " + string.Join(" | ",
            notices.Take(committedCount).Select(n => $"{n.Migration} ({n.Severity}): {n.Text}"));
    }

    private static void ExecuteNonQuery(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params NpgsqlParameter[] parameters)
    {
        using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static List<string> QueryMigrationNames(NpgsqlConnection conn)
    {
        var names = new List<string>();
        using var cmd = new NpgsqlCommand("SELECT name FROM _migrations ORDER BY name", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static IEnumerable<(string Name, string Sql)> GetMigrationFiles()
    {
        var assembly = typeof(Storage.Postgres.ServiceCollectionExtensions).Assembly;
        var prefix = "Verbara.Platform.Storage.Postgres.Migrations.";

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            var name = resourceName[prefix.Length..];
            yield return (name, sql);
        }
    }
}
