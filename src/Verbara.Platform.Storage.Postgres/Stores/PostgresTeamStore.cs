using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTeamStore : ITeamStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTeamStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Team?> GetByIdAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by " +
            "FROM teams WHERE tenant_id = @TenantId AND team_id = @TeamId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("TeamId", teamId.Value)); },
            TeamRow.Map, ct);
        return row?.ToTeam();
    }

    public async Task<PagedResult<Team>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM teams WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)), ct) ?? 0L);
        var rows = await _dataSource.QueryListAsync(
            "SELECT team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by " +
            "FROM teams WHERE tenant_id = @TenantId ORDER BY name LIMIT @Limit OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Limit", query.PageSize)); p.Add(new NpgsqlParameter("Offset", query.Offset)); },
            TeamRow.Map, ct);
        var items = rows.Select(r => r.ToTeam()).ToList();
        return new PagedResult<Team>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Team team, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO teams (team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@TeamId, @TenantId, @Name, @SupervisorId, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, team_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, supervisor_id = EXCLUDED.supervisor_id, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            p =>
            {
                p.Add(new NpgsqlParameter("TeamId", team.TeamId.Value));
                p.Add(new NpgsqlParameter("TenantId", team.TenantId.Value));
                p.Add(new NpgsqlParameter("Name", team.Name));
                p.Add(new NpgsqlParameter("SupervisorId", NpgsqlDbType.Text) { Value = (object?)team.SupervisorId?.Value ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", team.CreatedAt));
                p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = (object?)team.UpdatedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedBy", NpgsqlDbType.Text) { Value = (object?)team.CreatedBy ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = (object?)team.UpdatedBy ?? DBNull.Value });
            },
            ct);
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM teams WHERE tenant_id = @TenantId AND team_id = @TeamId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("TeamId", teamId.Value)); },
            ct);
    }

    private sealed class TeamRow
    {
        public string team_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string name { get; init; } = null!;
        public string? supervisor_id { get; init; }
        public DateTime created_at { get; init; }
        public DateTime? updated_at { get; init; }
        public string? created_by { get; init; }
        public string? updated_by { get; init; }

        public static TeamRow Map(NpgsqlDataReader r) => new()
        {
            team_id = r.GetString("team_id"),
            tenant_id = r.GetString("tenant_id"),
            name = r.GetString("name"),
            supervisor_id = r.GetStringOrNull("supervisor_id"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTimeOrNull("updated_at"),
            created_by = r.GetStringOrNull("created_by"),
            updated_by = r.GetStringOrNull("updated_by"),
        };

        public Team ToTeam() => new()
        {
            TeamId = EntityId.From(team_id),
            TenantId = new TenantId(tenant_id),
            Name = name,
            SupervisorId = supervisor_id != null ? EntityId.From(supervisor_id) : null,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
            CreatedBy = created_by,
            UpdatedBy = updated_by,
        };
    }
}
