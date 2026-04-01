using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAuthEventStore : IAuthEventStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuthEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AuthEvent authEvent, CancellationToken ct)
    {
        var detailsJson = authEvent.Details?.RootElement.GetRawText();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO auth_events (event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at) " +
            "VALUES (@EventId, @TenantId, @UserId, @EventType, @IpAddress, @UserAgent, @Details::jsonb, @CreatedAt)",
            new
            {
                authEvent.EventId,
                authEvent.TenantId,
                authEvent.UserId,
                authEvent.EventType,
                authEvent.IpAddress,
                authEvent.UserAgent,
                Details = detailsJson,
                authEvent.CreatedAt,
            });
    }

    public async Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM auth_events WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });

        var offset = (page - 1) * pageSize;
        var rows = await conn.QueryAsync<AuthEventRow>(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId, Limit = pageSize, Offset = offset });

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, page, pageSize);
    }

    public async Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });

        var offset = (page - 1) * pageSize;
        var rows = await conn.QueryAsync<AuthEventRow>(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            new { TenantId = tenantId, UserId = userId, Limit = pageSize, Offset = offset });

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, page, pageSize);
    }

    public async Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct)
    {
        var conditions = new List<string> { "tenant_id = @TenantId" };
        var parameters = new DynamicParameters();
        parameters.Add("TenantId", tenantId);

        if (!string.IsNullOrEmpty(query.UserId))
        {
            conditions.Add("user_id = @UserId");
            parameters.Add("UserId", query.UserId);
        }
        if (!string.IsNullOrEmpty(query.EventType))
        {
            conditions.Add("event_type = @EventType");
            parameters.Add("EventType", query.EventType);
        }
        if (query.StartDate.HasValue)
        {
            conditions.Add("created_at >= @StartDate");
            parameters.Add("StartDate", query.StartDate.Value);
        }
        if (query.EndDate.HasValue)
        {
            conditions.Add("created_at <= @EndDate");
            parameters.Add("EndDate", query.EndDate.Value);
        }

        var where = string.Join(" AND ", conditions);
        var offset = (query.Page - 1) * query.PageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM auth_events WHERE {where}", parameters);

        parameters.Add("Limit", query.PageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<AuthEventRow>(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            $"FROM auth_events WHERE {where} ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuthEventRow>(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY created_at DESC",
            new { TenantId = tenantId, UserId = userId });
        return rows.Select(r => r.ToAuthEvent()).ToList();
    }

    public async Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });
    }

    public async Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            new { TenantId = tenantId, Cutoff = cutoff });
    }

    private sealed record AuthEventRow(
        string event_id,
        string tenant_id,
        string? user_id,
        string event_type,
        string? ip_address,
        string? user_agent,
        string? details,
        DateTimeOffset created_at)
    {
        public AuthEvent ToAuthEvent()
        {
            JsonDocument? detailsDoc = null;
            if (!string.IsNullOrEmpty(details))
                detailsDoc = JsonDocument.Parse(details);

            return new AuthEvent
            {
                EventId = event_id,
                TenantId = tenant_id,
                UserId = user_id,
                EventType = event_type,
                IpAddress = ip_address,
                UserAgent = user_agent,
                Details = detailsDoc,
                CreatedAt = created_at,
            };
        }
    }
}
