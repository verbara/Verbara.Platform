using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers Postgres for add-user-roles-user-foreign-key: one container, and a fresh database per test
/// built from the REAL embedded <c>Migrations/*.sql</c> resources, either up to (not including)
/// <see cref="Migration024"/> — so a test can seed the orphan <c>user_roles</c> rows the migration must
/// remove — or through every migration.
/// </summary>
public sealed class UserRolesForeignKeyFixture : IAsyncLifetime
{
    public const string Migration024 = "024_UserRolesUserForeignKey.sql";

    private const string ResourcePrefix = "Verbara.Platform.Storage.Postgres.Migrations.";

    private IContainer? _container;
    private readonly List<NpgsqlDataSource> _dataSources = [];

    private string ConnectionStringFor(string database) =>
        $"Host={_container!.Hostname};Port={_container.GetMappedPublicPort(5432)};" +
        $"Database={database};Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    // `-h 127.0.0.1` forces the readiness probe over TCP (see MigrationsFixture).
                    .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var ds in _dataSources)
            await ds.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>
    /// A new, empty database with the migrations applied in name order: all of them, or only those
    /// before <see cref="Migration024"/> when <paramref name="before024"/> is set.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(bool before024)
    {
        var name = $"urfk_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();

        var ds = NpgsqlDataSource.Create(ConnectionStringFor(name));
        _dataSources.Add(ds);

        foreach (var (file, sql) in Migrations())
        {
            if (before024 && string.CompareOrdinal(file, Migration024) >= 0)
                break;
            await ExecuteAsync(ds, sql);
        }

        return ds;
    }

    /// <summary>Runs one embedded migration inside a transaction, as DatabaseMigrationService does.</summary>
    public static async Task ApplyMigrationAsync(NpgsqlDataSource ds, string file)
    {
        var sql = Migrations().Single(m => m.File == file).Sql;
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public static async Task ExecuteAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public static Task InsertUserAsync(NpgsqlDataSource ds, string tenantId, string userId) =>
        ExecuteWithAsync(ds,
            "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at) " +
            "VALUES (@UserId, @TenantId, @Email, @UserId, 0, 0, now())",
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Email", $"{userId}@{tenantId}.test"));

    public static Task InsertRoleAsync(NpgsqlDataSource ds, string tenantId, string roleId) =>
        ExecuteWithAsync(ds,
            "INSERT INTO tenant_roles (role_id, tenant_id, name) VALUES (@RoleId, @TenantId, @RoleId)",
            new NpgsqlParameter("RoleId", roleId),
            new NpgsqlParameter("TenantId", tenantId));

    public static Task InsertUserRoleAsync(NpgsqlDataSource ds, string tenantId, string userId, string roleId) =>
        ExecuteWithAsync(ds,
            "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_by) VALUES (@TenantId, @UserId, @RoleId, 'test')",
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("RoleId", roleId));

    /// <summary>Every row of <c>user_roles</c> as <c>tenant|user|role</c>, ordered.</summary>
    public static async Task<List<string>> AssignmentsAsync(NpgsqlDataSource ds)
    {
        var rows = new List<string>();
        await using var cmd = ds.CreateCommand(
            "SELECT tenant_id || '|' || user_id || '|' || role_id FROM user_roles ORDER BY 1");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>
    /// The foreign key of <c>user_roles</c> onto <c>users</c>: its name and its <c>ON DELETE</c> action
    /// (<c>c</c> = cascade), or <c>null</c> when there is none.
    /// </summary>
    public static async Task<(string Name, char OnDelete)?> UserForeignKeyAsync(NpgsqlDataSource ds)
    {
        await using var cmd = ds.CreateCommand(
            "SELECT conname, confdeltype FROM pg_constraint " +
            "WHERE contype = 'f' AND conrelid = 'user_roles'::regclass AND confrelid = 'users'::regclass");
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetChar(1));
    }

    private static async Task ExecuteWithAsync(NpgsqlDataSource ds, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static IEnumerable<(string File, string Sql)> Migrations()
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource[ResourcePrefix.Length..], reader.ReadToEnd());
        }
    }
}
