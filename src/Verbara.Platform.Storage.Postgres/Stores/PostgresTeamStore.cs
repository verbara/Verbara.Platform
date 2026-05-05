using Dapper;
using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTeamStore : ITeamStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTeamStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<Team?> GetByIdAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TeamRow>(
            "SELECT team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by " +
            "FROM teams WHERE tenant_id = @TenantId AND team_id = @TeamId",
            new { TenantId = tenantId.Value, TeamId = teamId.Value });
        return row?.ToTeam();
    }

    public async Task<PagedResult<Team>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM teams WHERE tenant_id = @TenantId",
            new { TenantId = tenantId.Value });
        var rows = await conn.QueryAsync<TeamRow>(
            "SELECT team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by " +
            "FROM teams WHERE tenant_id = @TenantId ORDER BY name LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId.Value, Limit = query.PageSize, Offset = query.Offset });
        var items = rows.Select(r => r.ToTeam()).ToList();
        return new PagedResult<Team>(items, total, query.Page, query.PageSize);
    }

    public async Task SaveAsync(Team team, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO teams (team_id, tenant_id, name, supervisor_id, created_at, updated_at, created_by, updated_by) " +
            "VALUES (@TeamId, @TenantId, @Name, @SupervisorId, @CreatedAt, @UpdatedAt, @CreatedBy, @UpdatedBy) " +
            "ON CONFLICT (tenant_id, team_id) DO UPDATE SET " +
            "  name = EXCLUDED.name, supervisor_id = EXCLUDED.supervisor_id, " +
            "  updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by",
            new
            {
                TeamId = team.TeamId.Value,
                TenantId = team.TenantId.Value,
                team.Name,
                SupervisorId = team.SupervisorId?.Value,
                team.CreatedAt,
                team.UpdatedAt,
                team.CreatedBy,
                team.UpdatedBy,
            });
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM teams WHERE tenant_id = @TenantId AND team_id = @TeamId",
            new { TenantId = tenantId.Value, TeamId = teamId.Value });
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
