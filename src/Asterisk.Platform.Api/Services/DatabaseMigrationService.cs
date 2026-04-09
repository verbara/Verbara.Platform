using System.Reflection;
using Dapper;
using Npgsql;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class DatabaseMigrationService : IHostedService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseMigrationService> _logger;

    public DatabaseMigrationService(NpgsqlDataSource dataSource, ILogger<DatabaseMigrationService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Create tracking table
        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS _migrations (name TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now())");

        // Get applied migrations
        var applied = (await conn.QueryAsync<string>("SELECT name FROM _migrations ORDER BY name"))
            .ToHashSet(StringComparer.Ordinal);

        // Get all migration files sorted by name
        var migrations = GetMigrationFiles().OrderBy(m => m.Name, StringComparer.Ordinal);

        foreach (var migration in migrations)
        {
            if (applied.Contains(migration.Name))
                continue;

            LogApplyingMigration(migration.Name);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await conn.ExecuteAsync(migration.Sql, transaction: tx);
                await conn.ExecuteAsync(
                    "INSERT INTO _migrations (name) VALUES (@Name)",
                    new { migration.Name }, tx);
                await tx.CommitAsync(ct);
                LogMigrationApplied(migration.Name);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                LogMigrationFailed(migration.Name, ex);
                throw new InvalidOperationException(
                    $"Migration '{migration.Name}' failed: {ex.Message}", ex);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

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
            var name = resourceName[prefix.Length..]; // e.g., "001_InitialSchema.sql"
            yield return (name, sql);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migration: {Name}")]
    private partial void LogApplyingMigration(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migration applied: {Name}")]
    private partial void LogMigrationApplied(string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Migration failed: {Name}")]
    private partial void LogMigrationFailed(string name, Exception ex);
}
