using Dapper;
using Npgsql;

namespace Asterisk.Platform.Api.Services;

internal static class DatabaseMigrationService
{
    /// <summary>
    /// Synchronously apply all pending Platform SQL migrations.
    /// Called early in Program.cs before Pro packages register
    /// (their EnsureSchemaAsync references Platform tables like contacts).
    /// </summary>
    public static void ApplyMigrations(string connectionString)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        using var conn = dataSource.OpenConnection();

        conn.Execute(
            "CREATE TABLE IF NOT EXISTS _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");

        var applied = conn.Query<string>("SELECT name FROM _migrations ORDER BY name")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var migration in GetMigrationFiles().OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (applied.Contains(migration.Name))
                continue;

            using var tx = conn.BeginTransaction();
            try
            {
                conn.Execute(migration.Sql, transaction: tx);
                conn.Execute(
                    "INSERT INTO _migrations (name) VALUES (@Name)",
                    new { migration.Name }, tx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                // Postgres aborts the transaction server-side on SQL error;
                // the client-side Rollback() then throws "transaction has completed".
                // Swallow that secondary exception so the real migration error surfaces.
                try { tx.Rollback(); }
                catch (InvalidOperationException) { /* tx already aborted by server */ }
                throw new InvalidOperationException(
                    $"Migration '{migration.Name}' failed: {ex.Message}", ex);
            }
        }

        dataSource.Dispose();
    }

    private static IEnumerable<(string Name, string Sql)> GetMigrationFiles()
    {
        var assembly = typeof(Storage.Postgres.ServiceCollectionExtensions).Assembly;
        var prefix = "Asterisk.Platform.Storage.Postgres.Migrations.";

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
