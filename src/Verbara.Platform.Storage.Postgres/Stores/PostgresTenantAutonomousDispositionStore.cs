using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed per-tenant autonomous-disposition activation gate (ADR-0034). One row per
/// tenant in <c>tenant_autonomous_disposition</c>; revocation is a soft-delete (sets
/// <c>revoked_at</c> = <c>now()</c> + <c>revoked_by_user_id</c>). A controller-instruction config
/// record — never the data subject's consent.
/// </summary>
internal sealed class PostgresTenantAutonomousDispositionStore : ITenantAutonomousDispositionStore
{
    private const string SelectColumns =
        "tenant_id, attested_by_user_id, attested_at, revoked_at, revoked_by_user_id";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantAutonomousDispositionStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantAutonomousDisposition?> GetAsync(TenantId tenantId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM tenant_autonomous_disposition WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            Row.Map, ct);
        return row?.ToRecord();
    }

    public async Task UpsertAsync(TenantAutonomousDisposition record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _dataSource.ExecuteAsync(
            "INSERT INTO tenant_autonomous_disposition " +
            "(tenant_id, attested_by_user_id, attested_at, revoked_at, revoked_by_user_id) " +
            "VALUES (@TenantId, @AttestedByUserId, @AttestedAt, @RevokedAt, @RevokedByUserId) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  attested_by_user_id = EXCLUDED.attested_by_user_id, " +
            "  attested_at = EXCLUDED.attested_at, " +
            "  revoked_at = EXCLUDED.revoked_at, " +
            "  revoked_by_user_id = EXCLUDED.revoked_by_user_id",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", record.TenantId.Value));
                p.Add(new NpgsqlParameter("AttestedByUserId", record.AttestedByUserId));
                p.Add(new NpgsqlParameter("AttestedAt", NpgsqlDbType.TimestampTz) { Value = record.AttestedAt.UtcDateTime });
                p.Add(new NpgsqlParameter("RevokedAt", NpgsqlDbType.TimestampTz) { Value = (object?)record.RevokedAt?.UtcDateTime ?? DBNull.Value });
                p.Add(new NpgsqlParameter("RevokedByUserId", NpgsqlDbType.Text) { Value = (object?)record.RevokedByUserId ?? DBNull.Value });
            },
            ct);
    }

    public async Task RevokeAsync(TenantId tenantId, EntityId revokedBy, CancellationToken ct)
    {
        // Soft-delete: stamp revoked_at with the DB clock so the timestamp is authoritative.
        // A no-op if no row exists (0 rows affected).
        await _dataSource.ExecuteAsync(
            "UPDATE tenant_autonomous_disposition SET revoked_at = now(), revoked_by_user_id = @RevokedBy " +
            "WHERE tenant_id = @TenantId AND revoked_at IS NULL",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("RevokedBy", revokedBy.Value));
            },
            ct);
    }

    private sealed class Row
    {
        public string tenant_id { get; init; } = null!;
        public string attested_by_user_id { get; init; } = null!;
        public DateTime attested_at { get; init; }
        public DateTime? revoked_at { get; init; }
        public string? revoked_by_user_id { get; init; }

        public static Row Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            attested_by_user_id = r.GetString("attested_by_user_id"),
            attested_at = r.GetDateTime("attested_at"),
            revoked_at = r.GetDateTimeOrNull("revoked_at"),
            revoked_by_user_id = r.GetStringOrNull("revoked_by_user_id"),
        };

        public TenantAutonomousDisposition ToRecord() => new()
        {
            TenantId = new TenantId(tenant_id),
            AttestedByUserId = attested_by_user_id,
            AttestedAt = new DateTimeOffset(attested_at, TimeSpan.Zero),
            RevokedAt = revoked_at is not null ? new DateTimeOffset(revoked_at.Value, TimeSpan.Zero) : null,
            RevokedByUserId = revoked_by_user_id,
        };
    }
}
