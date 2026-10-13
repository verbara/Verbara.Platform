using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed <see cref="ISupervisorListenProfileStore"/> (supervisor-silent-listen, design D12), on
/// <c>supervisor_listen_profiles</c> (migration 022). The extension is unique per tenant through the partial
/// unique index <c>ux_supervisor_listen_profiles_extension</c>.
/// </summary>
internal sealed class PostgresSupervisorListenProfileStore : ISupervisorListenProfileStore
{
    private const string ExtensionIndex = "ux_supervisor_listen_profiles_extension";

    private const string SelectColumns =
        "tenant_id, user_id, sip_extension, sip_password, phone_number, created_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresSupervisorListenProfileStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<SupervisorListenProfile?> GetAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var row = await _dataSource.QueryFirstOrDefaultAsync(
            $"SELECT {SelectColumns} FROM supervisor_listen_profiles WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            Row.Map, ct);
        return row?.ToProfile();
    }

    public async Task<IReadOnlyList<SupervisorListenProfile>> ListWithCredentialsAsync(TenantId tenantId, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            $"SELECT {SelectColumns} FROM supervisor_listen_profiles " +
            "WHERE tenant_id = @TenantId AND sip_extension IS NOT NULL AND sip_password IS NOT NULL ORDER BY user_id",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            Row.Map, ct);
        return [.. rows.Select(r => r.ToProfile())];
    }

    public async Task<bool> SaveAsync(SupervisorListenProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            await _dataSource.ExecuteAsync(
                "INSERT INTO supervisor_listen_profiles " +
                "(tenant_id, user_id, sip_extension, sip_password, phone_number, created_at, updated_at) " +
                "VALUES (@TenantId, @UserId, @SipExtension, @SipPassword, @PhoneNumber, @CreatedAt, @UpdatedAt) " +
                "ON CONFLICT (tenant_id, user_id) DO UPDATE SET " +
                "  sip_extension = EXCLUDED.sip_extension, " +
                "  sip_password = EXCLUDED.sip_password, " +
                "  phone_number = EXCLUDED.phone_number, " +
                "  updated_at = EXCLUDED.updated_at",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", profile.TenantId.Value));
                    p.Add(new NpgsqlParameter("UserId", profile.UserId.Value));
                    p.Add(new NpgsqlParameter("SipExtension", NpgsqlDbType.Varchar) { Value = (object?)profile.SipExtension ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("SipPassword", NpgsqlDbType.Varchar) { Value = (object?)profile.SipPassword ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("PhoneNumber", NpgsqlDbType.Varchar) { Value = (object?)profile.PhoneNumber ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = profile.CreatedAt.UtcDateTime });
                    p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = profile.UpdatedAt.UtcDateTime });
                },
                ct);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                            && string.Equals(ex.ConstraintName, ExtensionIndex, StringComparison.Ordinal))
        {
            return false;
        }
    }

    public async Task DeleteAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "DELETE FROM supervisor_listen_profiles WHERE tenant_id = @TenantId AND user_id = @UserId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("UserId", userId.Value));
            },
            ct);
    }

    private sealed class Row
    {
        public string tenant_id { get; init; } = null!;
        public string user_id { get; init; } = null!;
        public string? sip_extension { get; init; }
        public string? sip_password { get; init; }
        public string? phone_number { get; init; }
        public DateTime created_at { get; init; }
        public DateTime updated_at { get; init; }

        public static Row Map(NpgsqlDataReader r) => new()
        {
            tenant_id = r.GetString("tenant_id"),
            user_id = r.GetString("user_id"),
            sip_extension = r.GetStringOrNull("sip_extension"),
            sip_password = r.GetStringOrNull("sip_password"),
            phone_number = r.GetStringOrNull("phone_number"),
            created_at = r.GetDateTime("created_at"),
            updated_at = r.GetDateTime("updated_at"),
        };

        public SupervisorListenProfile ToProfile() => new()
        {
            TenantId = new TenantId(tenant_id),
            UserId = EntityId.From(user_id),
            SipExtension = sip_extension,
            SipPassword = sip_password,
            PhoneNumber = phone_number,
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(updated_at, TimeSpan.Zero),
        };
    }
}
