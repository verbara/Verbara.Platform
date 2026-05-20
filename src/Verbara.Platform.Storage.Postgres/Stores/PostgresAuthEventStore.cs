using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresAuthEventStore : IAuthEventStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAuthEventStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(AuthEvent authEvent, CancellationToken ct)
    {
        var detailsJson = authEvent.Details?.RootElement.GetRawText();

        await _dataSource.ExecuteAsync(
            "INSERT INTO auth_events (event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at) " +
            "VALUES (@EventId, @TenantId, @UserId, @EventType, @IpAddress, @UserAgent, @Details::jsonb, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("EventId", authEvent.EventId));
                p.Add(new NpgsqlParameter("TenantId", authEvent.TenantId));
                p.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = (object?)authEvent.UserId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("EventType", authEvent.EventType));
                p.Add(new NpgsqlParameter("IpAddress", NpgsqlDbType.Text) { Value = (object?)authEvent.IpAddress ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UserAgent", NpgsqlDbType.Text) { Value = (object?)authEvent.UserAgent ?? DBNull.Value });
                p.Add(new NpgsqlParameter("Details", (object?)detailsJson ?? DBNull.Value));
                p.Add(new NpgsqlParameter("CreatedAt", authEvent.CreatedAt));
            },
            ct);
    }

    public async Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM auth_events WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)), ct) ?? 0L);

        var offset = (page - 1) * pageSize;
        var rows = await _dataSource.QueryListAsync(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            AuthEventRow.Map, ct);

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, page, pageSize);
    }

    public async Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct)
    {
        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
            }, ct) ?? 0L);

        var offset = (page - 1) * pageSize;
        var rows = await _dataSource.QueryListAsync(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            AuthEventRow.Map, ct);

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, page, pageSize);
    }

    public async Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct)
    {
        var conditions = new List<string> { "tenant_id = @TenantId" };
        var binders = new List<Action<NpgsqlParameterCollection>>
        {
            p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
        };

        if (!string.IsNullOrEmpty(query.UserId))
        {
            conditions.Add("user_id = @UserId");
            binders.Add(p => p.Add(new NpgsqlParameter("UserId", query.UserId)));
        }
        if (!string.IsNullOrEmpty(query.EventType))
        {
            conditions.Add("event_type = @EventType");
            binders.Add(p => p.Add(new NpgsqlParameter("EventType", query.EventType)));
        }
        if (query.StartDate.HasValue)
        {
            conditions.Add("created_at >= @StartDate");
            binders.Add(p => p.Add(new NpgsqlParameter("StartDate", query.StartDate.Value)));
        }
        if (query.EndDate.HasValue)
        {
            conditions.Add("created_at <= @EndDate");
            binders.Add(p => p.Add(new NpgsqlParameter("EndDate", query.EndDate.Value)));
        }

        var where = string.Join(" AND ", conditions);
        var offset = (query.Page - 1) * query.PageSize;

        void BindFilters(NpgsqlParameterCollection p) { foreach (var b in binders) b(p); }

        var total = (int)(await _dataSource.ExecuteScalarAsync<long?>(
            $"SELECT COUNT(*) FROM auth_events WHERE {where}", BindFilters, ct) ?? 0L);

        var rows = await _dataSource.QueryListAsync(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            $"FROM auth_events WHERE {where} ORDER BY created_at DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                BindFilters(p);
                p.Add(new NpgsqlParameter("Limit", query.PageSize));
                p.Add(new NpgsqlParameter("Offset", offset));
            },
            AuthEventRow.Map, ct);

        var items = rows.Select(r => r.ToAuthEvent()).ToList();
        return new PagedResult<AuthEvent>(items, total, query.Page, query.PageSize);
    }

    public async Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY created_at DESC",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
            },
            AuthEventRow.Map, ct);
        return rows.Select(r => r.ToAuthEvent()).ToList();
    }

    public async Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("UserId", userId));
            },
            ct);
    }

    public async Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        return await _dataSource.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Cutoff", cutoff));
            },
            ct);
    }

    private sealed class AuthEventRow
    {
        public string event_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string? user_id { get; init; }
        public string event_type { get; init; } = null!;
        public string? ip_address { get; init; }
        public string? user_agent { get; init; }
        public string? details { get; init; }
        public DateTime created_at { get; init; }

        public static AuthEventRow Map(NpgsqlDataReader r) => new()
        {
            event_id = r.GetString("event_id"),
            tenant_id = r.GetString("tenant_id"),
            user_id = r.GetStringOrNull("user_id"),
            event_type = r.GetString("event_type"),
            ip_address = r.GetStringOrNull("ip_address"),
            user_agent = r.GetStringOrNull("user_agent"),
            details = r.GetStringOrNull("details"),
            created_at = r.GetDateTime("created_at"),
        };

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
