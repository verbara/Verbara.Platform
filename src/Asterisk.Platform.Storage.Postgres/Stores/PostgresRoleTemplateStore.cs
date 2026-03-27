using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresRoleTemplateStore : IRoleTemplateStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresRoleTemplateStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TemplateRow>(
            "SELECT template_id, name, description, is_system, created_at " +
            "FROM role_templates ORDER BY name");
        return rows.Select(r => r.ToTemplate()).ToList();
    }

    public async Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TemplateRow>(
            "SELECT template_id, name, description, is_system, created_at " +
            "FROM role_templates WHERE template_id = @TemplateId",
            new { TemplateId = templateId });
        if (row is null) return null;

        var template = row.ToTemplate();
        var permissions = await GetPermissionsInternalAsync(conn, templateId);
        template.Permissions = permissions;
        return template;
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await GetPermissionsInternalAsync(conn, templateId);
    }

    private static async Task<List<string>> GetPermissionsInternalAsync(
        System.Data.IDbConnection conn, string templateId)
    {
        var perms = await conn.QueryAsync<string>(
            "SELECT permission_id FROM role_template_permissions " +
            "WHERE template_id = @TemplateId ORDER BY permission_id",
            new { TemplateId = templateId });
        return perms.ToList();
    }

    private sealed record TemplateRow(
        string template_id, string name, string description,
        bool is_system, DateTimeOffset created_at)
    {
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
