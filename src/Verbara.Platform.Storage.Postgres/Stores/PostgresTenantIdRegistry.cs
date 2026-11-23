using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// <see cref="ITenantIdRegistry"/> over the <c>tenants</c> table (realtime-objects-stay-in-their-tenant, design D1):
/// one transaction at READ COMMITTED takes a transaction-scoped advisory lock on a constant key, scans every stored
/// id for an overlap, and inserts only when there is none.
/// </summary>
/// <remarks>
/// READ COMMITTED is named explicitly: each statement reads the rows committed before it started, so the scan, which
/// runs after the lock is granted, sees the previous holder's committed insert. Under REPEATABLE READ the snapshot is
/// taken at the lock call, before the wait, and the scan would miss it.
/// </remarks>
public sealed class PostgresTenantIdRegistry : ITenantIdRegistry
{
    /// <summary>The advisory-lock key every tenant create takes (<c>0x74656E616E746964</c>, "tenantid" in ASCII).</summary>
    public const long LockKey = 0x74656E616E746964;

    private const string OverlapScan =
        "SELECT tenant_id FROM tenants WHERE lower(tenant_id) = lower(@Id) " +
        "OR starts_with(lower(tenant_id), lower(@Id) || '-') " +
        "OR starts_with(lower(@Id), lower(tenant_id) || '-') LIMIT 1";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantIdRegistry(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async ValueTask<TenantCreateResult> TryCreateAsync(Tenant tenant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var lockCmd = Command(conn, tx, "SELECT pg_advisory_xact_lock(@Key)"))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter("Key", NpgsqlDbType.Bigint) { Value = LockKey });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var scan = Command(conn, tx, OverlapScan))
        {
            scan.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = tenant.TenantId });
            if (await scan.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return TenantCreateResult.Conflict;
        }

        await EnforceInvariantsAsync(conn, tx, tenant, ct).ConfigureAwait(false);

        var optionsJson = JsonSerializer.Serialize(tenant.Options, PostgresJson.Ctx.TenantOptions);
        var metadataJson = tenant.Metadata is not null
            ? JsonSerializer.Serialize(tenant.Metadata, PostgresJson.Ctx.DictionaryStringString)
            : null;
        await using (var insert = Command(conn, tx,
            "INSERT INTO tenants (tenant_id, name, status, type, parent_tenant_id, options, metadata, created_at, updated_at) " +
            "VALUES (@TenantId, @Name, @Status, @Type, @ParentTenantId, @Options::jsonb, @Metadata::jsonb, @CreatedAt, @UpdatedAt)"))
        {
            insert.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenant.TenantId });
            insert.Parameters.Add(new NpgsqlParameter("Name", NpgsqlDbType.Text) { Value = tenant.Name });
            insert.Parameters.Add(new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = (int)tenant.Status });
            insert.Parameters.Add(new NpgsqlParameter("Type", NpgsqlDbType.Integer) { Value = (int)tenant.Type });
            insert.Parameters.Add(new NpgsqlParameter("ParentTenantId", NpgsqlDbType.Text)
                { Value = (object?)tenant.ParentTenantId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("Options", NpgsqlDbType.Text) { Value = optionsJson });
            insert.Parameters.Add(new NpgsqlParameter("Metadata", NpgsqlDbType.Text) { Value = (object?)metadataJson ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = tenant.CreatedAt.UtcDateTime });
            insert.Parameters.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = tenant.UpdatedAt.UtcDateTime });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return TenantCreateResult.Created;
    }

    public async ValueTask<IReadOnlyList<StoredTenantId>> ListAllIdsAsync(CancellationToken ct)
    {
        return await _dataSource.QueryListAsync(
            "SELECT tenant_id, status FROM tenants ORDER BY tenant_id",
            _ => { },
            r => new StoredTenantId(r.GetString("tenant_id"), (TenantStatus)r.GetInt32("status")),
            ct).ConfigureAwait(false);
    }

    /// <summary>The invariants <see cref="PostgresTenantStore.UpsertAsync"/> enforces: one Platform tenant, depth 3.</summary>
    private static async Task EnforceInvariantsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Tenant tenant, CancellationToken ct)
    {
        if (tenant.Type == TenantType.Platform)
        {
            await using var cmd = Command(conn, tx, "SELECT tenant_id FROM tenants WHERE type = @Type LIMIT 1");
            cmd.Parameters.Add(new NpgsqlParameter("Type", NpgsqlDbType.Integer) { Value = (int)TenantType.Platform });
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existing && existing != tenant.TenantId)
                throw new InvalidOperationException("Only one Platform tenant is allowed.");
        }

        var ancestor = tenant.ParentTenantId;
        for (var depth = 1; ancestor is not null; depth++)
        {
            if (depth >= 3)
                throw new InvalidOperationException("Maximum tenant hierarchy depth (3 levels) exceeded.");
            await using var cmd = Command(conn, tx, "SELECT parent_tenant_id FROM tenants WHERE tenant_id = @TenantId");
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = ancestor });
            ancestor = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql) =>
        new(sql, conn, tx);
}
