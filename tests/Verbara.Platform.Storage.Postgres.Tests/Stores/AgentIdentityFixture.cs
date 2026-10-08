using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers Postgres for licensed-agent-metering slice 1 (agent-identity-integrity): one container,
/// and a fresh database per test built from the REAL embedded <c>Migrations/*.sql</c> resources, either
/// up to (not including) <c>018_AgentUserUnique.sql</c> — so a test can seed the duplicates the migration
/// must refuse — or through every migration.
/// </summary>
public sealed class AgentIdentityFixture : IAsyncLifetime
{
    public const string Migration018 = "018_AgentUserUnique.sql";

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
    /// before <see cref="Migration018"/> when <paramref name="before018"/> is set.
    /// </summary>
    public async Task<NpgsqlDataSource> CreateDatabaseAsync(bool before018)
    {
        var name = $"lam_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(ConnectionStringFor("postgres")))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
            await cmd.ExecuteNonQueryAsync();

        var ds = NpgsqlDataSource.Create(ConnectionStringFor(name));
        _dataSources.Add(ds);

        foreach (var (file, sql) in Migrations())
        {
            if (before018 && string.CompareOrdinal(file, Migration018) >= 0)
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

    public static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<bool> IndexExistsAsync(NpgsqlDataSource ds, string index)
    {
        await using var cmd = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @Name)");
        cmd.Parameters.Add(new NpgsqlParameter("Name", index));
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public static Task InsertAgentAsync(
        NpgsqlDataSource ds, string tenantId, string agentId, string userId, string? extension = null,
        DateTimeOffset? createdAt = null) =>
        ExecuteWithAsync(ds,
            "INSERT INTO agents (agent_id, tenant_id, user_id, display_name, extension, created_at) " +
            "VALUES (@AgentId, @TenantId, @UserId, @AgentId, @Extension, @CreatedAt)",
            new NpgsqlParameter("AgentId", agentId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("Extension", NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)extension ?? DBNull.Value },
            new NpgsqlParameter("CreatedAt", createdAt ?? DateTimeOffset.UtcNow));

    public static Task InsertUserAsync(NpgsqlDataSource ds, string tenantId, string userId, int status) =>
        ExecuteWithAsync(ds,
            "INSERT INTO users (user_id, tenant_id, email, display_name, role, status, created_at) " +
            "VALUES (@UserId, @TenantId, @Email, @UserId, 0, @Status, now())",
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Email", $"{userId}@{tenantId}.test"),
            new NpgsqlParameter("Status", NpgsqlTypes.NpgsqlDbType.Integer) { Value = status });

    public static Task InsertTenantAsync(NpgsqlDataSource ds, string tenantId, int type, string? parent) =>
        ExecuteWithAsync(ds,
            "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, created_at, updated_at) " +
            "VALUES (@TenantId, @TenantId, 0, @Type, @Parent, now(), now())",
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Type", NpgsqlTypes.NpgsqlDbType.Integer) { Value = type },
            new NpgsqlParameter("Parent", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)parent ?? DBNull.Value });

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
