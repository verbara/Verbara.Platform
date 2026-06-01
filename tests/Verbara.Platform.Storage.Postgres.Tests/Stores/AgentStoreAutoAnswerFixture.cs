using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed Postgres fixture for <see cref="PostgresAgentStoreAutoAnswerTests"/>.
/// Spins up postgres with the <c>agents</c> table from migration 001 plus the <c>auto_answer</c>
/// column from migration 028, so the column-projection of GetByUserIdAsync (the 3B.2b sip+auto_answer
/// fix that the InMemory store cannot catch) can be exercised against a real DB.
/// </summary>
public sealed class AgentStoreAutoAnswerFixture : IAsyncLifetime
{
    private IContainer? _container;

    public string ConnectionString =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "postgres"))
            .Build();

        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE agents RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // agents DDL from 001_InitialSchema.sql + the 028_VoiceAutoAnswer delta (auto_answer boolean NULL).
    private const string SchemaSql = """
        CREATE TABLE agents (
            agent_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            state INTEGER NOT NULL DEFAULT 0,
            capacity JSONB NOT NULL DEFAULT '{}',
            team_id TEXT,
            skills JSONB NOT NULL DEFAULT '[]',
            extension VARCHAR(20),
            sip_password VARCHAR(80),
            auto_answer BOOLEAN,
            created_at TIMESTAMPTZ NOT NULL,
            updated_at TIMESTAMPTZ,
            created_by TEXT,
            updated_by TEXT,
            PRIMARY KEY (tenant_id, agent_id)
        );
        CREATE INDEX idx_agents_user ON agents (tenant_id, user_id);
        """;
}
