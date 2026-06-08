using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for
/// <see cref="ApiKeyStoreLastUsedTests"/>. Spins up <c>postgres:16-alpine</c>
/// and creates only the <c>api_keys</c> table — column shape mirrors the final
/// folded table in the consolidated 001_Baseline.sql (incl. the R5.2 PC.5 / B.12
/// <c>last_used_at</c> column). Avoids dragging in the entire platform schema.
/// </summary>
public sealed class ApiKeyStoreLastUsedFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        await _container.StartAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Truncate api_keys between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE api_keys";
        await cmd.ExecuteNonQueryAsync();
    }

    // Subset of the consolidated 001_Baseline.sql api_keys table (final folded shape,
    // incl. key_type + last_used_at). Inlined so the test doesn't have to apply the
    // full migration pipeline (and its cross-package deps) to exercise a single store method.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS api_keys (
            key_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            key_hash TEXT NOT NULL,
            name TEXT NOT NULL,
            scopes TEXT NOT NULL,
            rate_limit_per_minute INTEGER,
            is_revoked BOOLEAN NOT NULL DEFAULT false,
            key_type INTEGER NOT NULL DEFAULT 0,
            user_id TEXT,
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ,
            expires_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            last_used_at TIMESTAMPTZ NULL,
            PRIMARY KEY (tenant_id, key_id)
        );
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("ApiKeyStoreLastUsed")]
public class ApiKeyStoreLastUsedCollection : ICollectionFixture<ApiKeyStoreLastUsedFixture>;
#pragma warning restore CA1711
