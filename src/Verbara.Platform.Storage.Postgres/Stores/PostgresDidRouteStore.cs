using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresDidRouteStore : IDidRouteStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDidRouteStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "route_id, tenant_id, did, queue_id, is_active, created_at, updated_at";

    public async Task<IReadOnlyList<DidRoute>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM did_routes WHERE tenant_id = @TenantId ORDER BY did",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            DidRouteRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<DidRoute>> ListActiveAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM did_routes WHERE tenant_id = @TenantId AND is_active ORDER BY did",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            DidRouteRow.Map, ct);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<DidRoute?> GetAsync(TenantId tenantId, EntityId routeId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM did_routes WHERE tenant_id = @TenantId AND route_id = @RouteId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("RouteId", routeId.Value)); },
            DidRouteRow.Map, ct);
        return row?.ToModel();
    }

    public async Task<DidRoute?> GetByDidAsync(TenantId tenantId, string did, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM did_routes WHERE tenant_id = @TenantId AND did = @Did",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("Did", did)); },
            DidRouteRow.Map, ct);
        return row?.ToModel();
    }

    public async Task<DidRoute> CreateAsync(DidRoute route, CancellationToken ct)
    {
        var routeId = route.RouteId.Value;
        if (string.IsNullOrWhiteSpace(routeId))
            routeId = EntityId.New().Value;

        try
        {
            var row = await _dataSource.QuerySingleAsync(
                "INSERT INTO did_routes (route_id, tenant_id, did, queue_id, is_active, created_at) " +
                "VALUES (@RouteId, @TenantId, @Did, @QueueId, @IsActive, @CreatedAt) " +
                $"RETURNING {SelectColumns}",
                p =>
                {
                    p.Add(new NpgsqlParameter("RouteId", routeId));
                    p.Add(new NpgsqlParameter("TenantId", route.TenantId.Value));
                    p.Add(new NpgsqlParameter("Did", route.Did));
                    p.Add(new NpgsqlParameter("QueueId", route.QueueId.Value));
                    p.Add(new NpgsqlParameter("IsActive", route.IsActive));
                    p.Add(new NpgsqlParameter("CreatedAt", route.CreatedAt));
                },
                DidRouteRow.Map, ct);
            return row.ToModel();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique violation on (tenant_id, did) — surface as a domain-meaningful
            // signal so the API returns HTTP 409 instead of a raw 500.
            throw new InvalidOperationException(
                $"A DID route already exists for did '{route.Did}' in tenant '{route.TenantId.Value}'.", ex);
        }
    }

    public async Task UpdateAsync(DidRoute route, CancellationToken ct)
    {
        try
        {
            await _dataSource.ExecuteAsync(
                "UPDATE did_routes SET did = @Did, queue_id = @QueueId, is_active = @IsActive, updated_at = now() " +
                "WHERE tenant_id = @TenantId AND route_id = @RouteId",
                p =>
                {
                    p.Add(new NpgsqlParameter("Did", route.Did));
                    p.Add(new NpgsqlParameter("QueueId", route.QueueId.Value));
                    p.Add(new NpgsqlParameter("IsActive", route.IsActive));
                    p.Add(new NpgsqlParameter("TenantId", route.TenantId.Value));
                    p.Add(new NpgsqlParameter("RouteId", route.RouteId.Value));
                },
                ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Renaming onto another route's DID violates UNIQUE (tenant_id, did) —
            // surface as the domain conflict signal so the API returns 409, not 500.
            throw new InvalidOperationException(
                $"A DID route already exists for did '{route.Did}' in tenant '{route.TenantId.Value}'.", ex);
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId routeId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM did_routes WHERE tenant_id = @TenantId AND route_id = @RouteId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("RouteId", routeId.Value)); },
            ct);
    }

    private sealed class DidRouteRow
    {
        public string route_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public string did { get; init; } = null!;
        public string queue_id { get; init; } = null!;
        public bool is_active { get; init; }
        public DateTimeOffset created_at { get; init; }
        public DateTimeOffset? updated_at { get; init; }

        public static DidRouteRow Map(NpgsqlDataReader r) => new()
        {
            route_id = r.GetString("route_id"),
            tenant_id = r.GetString("tenant_id"),
            did = r.GetString("did"),
            queue_id = r.GetString("queue_id"),
            is_active = r.GetBoolean("is_active"),
            created_at = r.GetDateTimeOffset("created_at"),
            updated_at = r.GetDateTimeOffsetOrNull("updated_at"),
        };

        public DidRoute ToModel() => new()
        {
            RouteId = EntityId.From(route_id),
            TenantId = new TenantId(tenant_id),
            Did = did,
            QueueId = EntityId.From(queue_id),
            IsActive = is_active,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }
}
