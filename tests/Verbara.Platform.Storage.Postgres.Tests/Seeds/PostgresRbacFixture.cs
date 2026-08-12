using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// Testcontainers-backed Postgres fixture shared by <see cref="RoleTemplateSeederReseedTests"/> and
/// <see cref="RbacSeedIntegrityTests"/>. Spins up <c>postgres:16-alpine</c> and creates the minimal
/// subset of tables touched by the RBAC seeding path — <c>permissions</c>, <c>role_templates</c>,
/// <c>role_template_permissions</c>, <c>tenant_roles</c>, <c>tenant_role_permissions</c>,
/// <c>user_roles</c> and <c>users</c> — rather than dragging in the entire platform schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fidelity over minimalism where constraints are concerned.</b> The point of running against a
/// real Postgres is that the real <em>constraints</em> fire, so every table here reproduces its
/// baseline primary keys, foreign keys <b>and unique indexes</b> verbatim. In particular
/// <c>idx_tenant_roles_name</c> — the second unique index on <c>tenant_roles</c>, over
/// <c>(tenant_id, lower(name))</c> — is load-bearing: while it was missing from this fixture, a
/// <c>tenant_roles</c> insert that conflicted on the role <em>name</em> rather than on the role
/// <em>id</em> could not be reproduced at all, and the production defect it causes in
/// <c>RbacMigrationSeeder.MigrateExistingUsersAsync</c> was invisible to the suite. Do not drop it.
/// </para>
/// <para>
/// The <c>users</c> table is deliberately a narrow projection of its baseline counterpart:
/// <c>MigrateExistingUsersAsync</c> reads only <c>tenant_id</c>, <c>user_id</c> and the integer
/// <c>role</c> enum (0=Agent, 1=Supervisor, 2=Admin, 3=Api). The other ~20 baseline columns
/// (credentials, MFA, OIDC, audit stamps) carry no RBAC meaning and would only couple this fixture
/// to unrelated schema churn.
/// </para>
/// </remarks>
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
            "role_template_permissions, role_templates, permissions, users RESTART IDENTITY CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }

    // Subset of the consolidated 001_Baseline.sql limited to the RBAC tables exercised by
    // ReseedExistingTenantsAsync and RbacMigrationSeeder.MigrateExistingUsersAsync. Keeping it
    // inline avoids coupling the test to the full migration pipeline + the ProMultiTenant deps.
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

        -- Second unique index on tenant_roles, verbatim from 001_Baseline.sql. Role names are
        -- unique per tenant case-insensitively, INDEPENDENTLY of role_id, so an insert can conflict
        -- here while missing a (tenant_id, role_id) conflict target entirely. Omitting it makes
        -- name-collision defects unreproducible; see the fixture remarks.
        CREATE UNIQUE INDEX IF NOT EXISTS idx_tenant_roles_name
            ON tenant_roles (tenant_id, lower(name));

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

        -- Narrow projection of baseline `users`: RbacMigrationSeeder.MigrateExistingUsersAsync
        -- drives its whole loop off "SELECT DISTINCT tenant_id FROM users" plus
        -- "SELECT user_id, role FROM users WHERE tenant_id = @TenantId". `role` is the UserRole
        -- enum stored as an integer: 0=Agent, 1=Supervisor, 2=Admin, 3=Api.
        CREATE TABLE IF NOT EXISTS users (
            user_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            role INTEGER NOT NULL,
            PRIMARY KEY (tenant_id, user_id)
        );
        """;
}

#pragma warning disable CA1711 // Identifiers should not have incorrect suffix - xunit convention
[CollectionDefinition("PostgresRbac")]
public class PostgresRbacCollection : ICollectionFixture<PostgresRbacFixture>;
#pragma warning restore CA1711
