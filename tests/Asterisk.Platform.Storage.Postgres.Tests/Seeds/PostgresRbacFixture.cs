using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Asterisk.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// Testcontainers-backed Postgres fixture used by
/// <see cref="RoleTemplateSeederReseedTests"/>. Spins up <c>postgres:16-alpine</c>
/// and creates the minimal subset of tables touched by
/// <c>RoleTemplateSeeder.ReseedExistingTenantsAsync</c>: <c>permissions</c>,
/// <c>role_templates</c>, <c>role_template_permissions</c>, <c>tenant_roles</c>,
/// <c>tenant_role_permissions</c>. Avoids dragging in the entire 19-migration
/// platform schema.
/// </summary>
public sealed class PostgresRbacFixture : IAsyncLifetime
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

    /// <summary>Truncate RBAC tables between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "TRUNCATE tenant_role_permissions, user_roles, tenant_roles, " +
            "role_template_permissions, role_templates, permissions RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // Subset of 001_InitialSchema.sql limited to the RBAC tables exercised by
    // ReseedExistingTenantsAsync. Keeping it inline avoids coupling the test
    // to the full 19-migration pipeline + the ProMultiTenant deps.
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS permissions (
            permission_id TEXT PRIMARY KEY,
            category TEXT NOT NULL,
            resource TEXT NOT NULL,
            action TEXT NOT NULL,
            description TEXT NOT NULL,
            implies TEXT[]
        );

        CREATE TABLE IF NOT EXISTS role_templates (
            template_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT NOT NULL,
            is_system BOOLEAN NOT NULL DEFAULT true,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS role_template_permissions (
            template_id TEXT NOT NULL REFERENCES role_templates(template_id) ON DELETE CASCADE,
            permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            PRIMARY KEY (template_id, permission_id)
        );

        CREATE TABLE IF NOT EXISTS tenant_roles (
            role_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            name TEXT NOT NULL,
            description TEXT,
            source_template_id TEXT,
            is_default BOOLEAN NOT NULL DEFAULT false,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at TIMESTAMPTZ,
            PRIMARY KEY (tenant_id, role_id)
        );

        CREATE TABLE IF NOT EXISTS tenant_role_permissions (
            tenant_id TEXT NOT NULL,
            role_id TEXT NOT NULL,
            permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
            PRIMARY KEY (tenant_id, role_id, permission_id),
            FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS user_roles (
            tenant_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            role_id TEXT NOT NULL,
            assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            assigned_by TEXT,
            PRIMARY KEY (tenant_id, user_id, role_id),
            FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
        );
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("PostgresRbac")]
public class PostgresRbacCollection : ICollectionFixture<PostgresRbacFixture>;
#pragma warning restore CA1711
