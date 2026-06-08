using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTypificationSchemaStore : ITypificationSchemaStore
{
    private const string SelectColumns =
        "schema_id, tenant_id, name, version, is_published, max_depth, definition, created_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTypificationSchemaStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TypificationSchema?> GetByIdAsync(TenantId tenantId, EntityId schemaId, int? version, CancellationToken ct)
    {
        if (version is { } v)
        {
            var exact = await _dataSource.QueryFirstOrDefaultAsync(
                $"SELECT {SelectColumns} FROM typification_schemas " +
                "WHERE tenant_id = @TenantId AND schema_id = @SchemaId AND version = @Version",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
                    p.Add(new NpgsqlParameter("Version", v));
                },
                SchemaRow.Map, ct);
            return exact?.ToSchema();
        }

        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_schemas " +
            "WHERE tenant_id = @TenantId AND schema_id = @SchemaId " +
            "ORDER BY version DESC LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
            },
            SchemaRow.Map, ct);
        return row?.ToSchema();
    }

    public async Task<TypificationSchema?> GetLatestPublishedAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM typification_schemas " +
            "WHERE tenant_id = @TenantId AND schema_id = @SchemaId AND is_published " +
            "ORDER BY version DESC LIMIT 1",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
            },
            SchemaRow.Map, ct);
        return row?.ToSchema();
    }

    public async Task<IReadOnlyList<TypificationSchema>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT DISTINCT ON (schema_id) {SelectColumns} FROM typification_schemas " +
            "WHERE tenant_id = @TenantId " +
            "ORDER BY schema_id, version DESC",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            SchemaRow.Map, ct);
        return rows.Select(r => r.ToSchema()).ToList();
    }

    public async Task SaveAsync(TypificationSchema schema, CancellationToken ct)
    {
        var definition = new TypificationSchemaDefinition(schema.Nodes, schema.Fields, schema.DataDips, schema.AiConfig);
        var definitionJson = JsonSerializer.Serialize(definition, PostgresJson.Ctx.TypificationSchemaDefinition);

        await _dataSource.ExecuteAsync(
            "INSERT INTO typification_schemas " +
            "(schema_id, tenant_id, name, version, is_published, max_depth, definition, created_at, updated_at) " +
            "VALUES (@SchemaId, @TenantId, @Name, @Version, @IsPublished, @MaxDepth, @Definition::jsonb, @CreatedAt, @UpdatedAt) " +
            "ON CONFLICT (tenant_id, schema_id, version) DO UPDATE SET " +
            "  name = EXCLUDED.name, is_published = EXCLUDED.is_published, max_depth = EXCLUDED.max_depth, " +
            "  definition = EXCLUDED.definition, created_at = EXCLUDED.created_at, updated_at = EXCLUDED.updated_at",
            p =>
            {
                p.Add(new NpgsqlParameter("SchemaId", schema.SchemaId.Value));
                p.Add(new NpgsqlParameter("TenantId", schema.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", schema.Name));
                p.Add(new NpgsqlParameter("Version", schema.Version));
                p.Add(new NpgsqlParameter("IsPublished", schema.IsPublished));
                p.Add(new NpgsqlParameter("MaxDepth", schema.MaxDepth));
                p.Add(new NpgsqlParameter("Definition", definitionJson));
                p.Add(new NpgsqlParameter("CreatedAt", schema.CreatedAt.UtcDateTime));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)schema.UpdatedAt?.UtcDateTime ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM typification_schemas WHERE tenant_id = @TenantId AND schema_id = @SchemaId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("SchemaId", schemaId.Value));
            },
            ct);
    }

    private sealed class SchemaRow
    {
        public string schema_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public int version { get; init; }
        public bool is_published { get; init; }
        public int max_depth { get; init; }
        public string definition { get; init; } = null!;
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }

        public static SchemaRow Map(NpgsqlDataReader r) => new()
        {
            schema_id = r.GetString("schema_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            version = r.GetInt32("version"),
            is_published = r.GetBoolean("is_published"),
            max_depth = r.GetInt32("max_depth"),
            definition = r.GetString("definition"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
        };

        public TypificationSchema ToSchema()
        {
            var def = JsonSerializer.Deserialize(definition, PostgresJson.Ctx.TypificationSchemaDefinition);
            return new TypificationSchema
            {
                SchemaId = EntityId.From(schema_id),
                TenantId = new TenantId(tenant_id),
                Name = name,
                Version = version,
                IsPublished = is_published,
                MaxDepth = max_depth,
                Nodes = def?.Nodes ?? (IReadOnlyList<TypificationNode>)[],
                Fields = def?.Fields ?? (IReadOnlyList<TypificationField>)[],
                DataDips = def?.DataDips ?? (IReadOnlyList<DataDipDef>)[],
                AiConfig = def?.AiConfig ?? new TypificationAiConfig
                {
                    EntityFieldMap = new Dictionary<string, string>(),
                },
                CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
                UpdatedAt = updated_at is { } u ? new DateTimeOffset(u, TimeSpan.Zero) : null,
            };
        }
    }
}
