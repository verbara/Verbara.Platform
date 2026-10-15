using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Identity.Mfa;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres <see cref="IRecoveryCodeCensus"/>: walks <c>users</c> in keyset batches and classifies each
/// stored recovery-code array after unwrapping it.
/// </summary>
/// <remarks>
/// <para>
/// <c>mfa_recovery_codes</c> is DataProtection-wrapped per element, so SQL cannot see a digest's family
/// and the count needs no schema change only because it unwraps. Each row is unwrapped with
/// <see cref="PostgresUserStore.UnprotectRecoveryCodes"/>, the store's own read path, so an element
/// the <see cref="UserMfaEncryptionMigrator"/> has not wrapped yet is classified as itself.
/// </para>
/// <para>
/// Only users whose MFA is on are counted: codes written by an enrollment that was never confirmed are
/// not redeemable and cannot be regenerated, so counting them would keep the legacy series from draining.
/// </para>
/// <para>
/// The walk mirrors the migrator: a keyset cursor over <c>(tenant_id, user_id)</c>, <see cref="BatchSize"/>
/// rows at a time, so a large <c>users</c> table is never materialised at once. It only reads. Cost is one
/// unwrap per element (microseconds each); the caller runs it on a slow timer. Nothing is logged here, and
/// the result carries counts only.
/// </para>
/// </remarks>
internal sealed class PostgresRecoveryCodeCensus : IRecoveryCodeCensus
{
    /// <summary>Rows per keyset batch.</summary>
    private const int BatchSize = 500;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresUserStore _unwrapper;

    public PostgresRecoveryCodeCensus(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _dataSource = dataSource;
        // The store owns the protector purpose and the per-element "still unwrapped" fallback; reusing its
        // read path keeps the census from drifting away from what redemption actually sees.
        _unwrapper = new PostgresUserStore(dataSource, dataProtectionProvider);
    }

    public async Task<RecoveryCodeCensus> CountAsync(CancellationToken ct)
    {
        long onArgon2id = 0;
        long onLegacy = 0;

        // ('', '') sorts before every real key: tenant_id and user_id are non-null TEXT.
        var lastTenantId = string.Empty;
        var lastUserId = string.Empty;

        while (true)
        {
            var cursorTenantId = lastTenantId;
            var cursorUserId = lastUserId;

            var rows = await _dataSource.QueryListAsync(
                ScanSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("LastTenantId", NpgsqlDbType.Text) { Value = cursorTenantId });
                    p.Add(new NpgsqlParameter("LastUserId", NpgsqlDbType.Text) { Value = cursorUserId });
                    p.Add(new NpgsqlParameter("BatchSize", NpgsqlDbType.Integer) { Value = BatchSize });
                },
                CensusRow.Map, ct);

            if (rows.Count == 0)
                break;

            foreach (var row in rows)
            {
                var codes = _unwrapper.UnprotectRecoveryCodes(row.MfaRecoveryCodes);
                if (codes is not { Length: > 0 })
                    continue;

                if (RecoveryCodeFamilies.HoldsLegacy(codes))
                    onLegacy++;
                else
                    onArgon2id++;
            }

            lastTenantId = rows[^1].TenantId;
            lastUserId = rows[^1].UserId;

            if (rows.Count < BatchSize)
                break;
        }

        return new RecoveryCodeCensus(onArgon2id, onLegacy);
    }

    private const string ScanSql =
        "SELECT tenant_id, user_id, mfa_recovery_codes " +
        "FROM users " +
        "WHERE mfa_enabled AND mfa_recovery_codes IS NOT NULL AND cardinality(mfa_recovery_codes) > 0 " +
        "  AND (tenant_id, user_id) > (@LastTenantId, @LastUserId) " +
        "ORDER BY tenant_id, user_id " +
        "LIMIT @BatchSize";

    private sealed class CensusRow
    {
        public string TenantId { get; init; } = null!;
        public string UserId { get; init; } = null!;
        public string[]? MfaRecoveryCodes { get; init; }

        public static CensusRow Map(NpgsqlDataReader r) => new()
        {
            TenantId = r.GetString("tenant_id"),
            UserId = r.GetString("user_id"),
            MfaRecoveryCodes = r.IsDBNull(r.GetOrdinal("mfa_recovery_codes"))
                ? null
                : r.GetFieldValue<string[]>(r.GetOrdinal("mfa_recovery_codes")),
        };
    }
}
