using Npgsql;

namespace Verbara.Platform.Storage.Postgres;

/// <summary>A notice a migration raised while the runner applied it (<c>RAISE NOTICE</c>, <c>RAISE WARNING</c>, ...).</summary>
/// <param name="Migration">The migration file the notice came from, e.g. <c>035_SurveyResponseOnePerDispatch.sql</c>.</param>
/// <param name="Severity">The Postgres severity, untranslated (<c>NOTICE</c>, <c>WARNING</c>, <c>INFO</c>, ...).</param>
/// <param name="Text">The notice text.</param>
public sealed record MigrationNotice(string Migration, string Severity, string Text)
{
    /// <summary>Whether Postgres raised it as a <c>WARNING</c>; every other severity is informational.</summary>
    public bool IsWarning => string.Equals(Severity, "WARNING", StringComparison.Ordinal);

    /// <summary>The notice as the Api logs it and the CLI prints it: its migration, its severity and its text.</summary>
    public override string ToString() => $"Migration {Migration} reported ({Severity}): {Text}";
}

/// <summary>What the runner reports while it works through the migrations, in order.</summary>
public interface IMigrationProgress
{
    /// <summary><paramref name="migration"/> is already recorded in <c>_migrations</c> and is not run again.</summary>
    void Skipped(string migration);

    /// <summary><paramref name="migration"/> was applied and recorded; <paramref name="notices"/> are the ones it raised.</summary>
    void Applied(string migration, IReadOnlyList<MigrationNotice> notices);

    /// <summary>
    /// <paramref name="migration"/> failed with <paramref name="exception"/> and was rolled back; the runner throws right after. Its notices are not
    /// reported: nothing it did was kept, and it raises them again when it is applied.
    /// </summary>
    void Failed(string migration, Exception exception);
}

/// <summary>
/// The one runner of the Platform's embedded SQL migrations, used by the Api at start-up and by the CLI's
/// <c>migrate</c> command (#711): each pending migration, in ordinal name order, in its own transaction together with
/// its row in <c>_migrations</c>, keeping every notice it raised (#701).
/// </summary>
public static class PlatformMigrationRunner
{
    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    /// <summary>Applies every pending embedded migration; see <see cref="Apply(string, IEnumerable{ValueTuple{string, string}}, IMigrationProgress?)"/>.</summary>
    public static IReadOnlyList<MigrationNotice> Apply(string connectionString, IMigrationProgress? progress = null) =>
        Apply(connectionString, EmbeddedMigrations(), progress);

    /// <summary>
    /// Applies the <paramref name="migrations"/> (name, SQL) not yet recorded in <c>_migrations</c>, in ordinal name
    /// order, and returns every notice the applied ones raised, in order.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A migration failed. It was rolled back; the ones before it stay applied, and the message carries their notices
    /// for a host that stops before its log exists (they are not raised again on the next run).
    /// </exception>
    public static IReadOnlyList<MigrationNotice> Apply(
        string connectionString, IEnumerable<(string Name, string Sql)> migrations, IMigrationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(migrations);

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

        foreach (var migration in migrations.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (applied.Contains(migration.Name))
            {
                progress?.Skipped(migration.Name);
                continue;
            }

            using var tx = conn.BeginTransaction();
            var raisedBefore = notices.Count;
            try
            {
                current = migration.Name;
                ExecuteNonQuery(conn, tx, migration.Sql);
                current = null;
                // applied_at is written explicitly: a table the CLI created before #711 has no default on it.
                ExecuteNonQuery(conn, tx,
                    "INSERT INTO _migrations (name, applied_at) VALUES (@Name, now())",
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
                progress?.Failed(migration.Name, ex);
                throw new InvalidOperationException(
                    $"Migration '{migration.Name}' failed: {ex.Message}{DescribeCommitted(notices, raisedBefore)}", ex);
            }

            progress?.Applied(migration.Name, notices.GetRange(raisedBefore, notices.Count - raisedBefore));
        }

        return notices;
    }

    /// <summary>The migrations embedded in this assembly, as (file name, SQL), in ordinal name order.</summary>
    public static IReadOnlyList<(string Name, string Sql)> EmbeddedMigrations()
    {
        var assembly = typeof(PlatformMigrationRunner).Assembly;
        var migrations = new List<(string Name, string Sql)>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            migrations.Add((resourceName[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return migrations;
    }

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
}
