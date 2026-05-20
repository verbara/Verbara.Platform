using Npgsql;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresRoleTemplateStore : IRoleTemplateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRoleTemplateStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT template_id, name, description, is_system, created_at " +
            "FROM role_templates ORDER BY name",
            p => { },
            TemplateRow.Map, ct);
        return rows.Select(r => r.ToTemplate()).ToList();
    }

    public async Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT template_id, name, description, is_system, created_at " +
            "FROM role_templates WHERE template_id = @TemplateId",
            p => p.Add(new NpgsqlParameter("TemplateId", templateId)),
            TemplateRow.Map, ct);
        if (row is null) return null;

        var template = row.ToTemplate();
        var permissions = await GetPermissionsListAsync(templateId, ct);
        template.Permissions = permissions;
        return template;
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct)
    {
        return await GetPermissionsListAsync(templateId, ct);
    }

    private async Task<List<string>> GetPermissionsListAsync(string templateId, CancellationToken ct)
    {
        return await _dataSource.QueryListAsync(
            "SELECT permission_id FROM role_template_permissions " +
            "WHERE template_id = @TemplateId ORDER BY permission_id",
            p => p.Add(new NpgsqlParameter("TemplateId", templateId)),
            static r => r.GetString("permission_id"), ct);
    }

    private sealed class TemplateRow
    {
        public string template_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string description { get; init; } = null!;
        public bool is_system { get; init; }
        public DateTime created_at { get; init; }

        public static TemplateRow Map(NpgsqlDataReader r) => new()
        {
            template_id = r.GetString("template_id"),
            name = r.GetString("name"),
            description = r.GetString("description"),
            is_system = r.GetBoolean("is_system"),
            created_at = r.GetDateTime("created_at"),
        };

        public RoleTemplate ToTemplate() => new()
        {
            TemplateId = template_id,
            Name = name,
            Description = description,
            IsSystem = is_system,
            CreatedAt = created_at,
        };
    }
}
