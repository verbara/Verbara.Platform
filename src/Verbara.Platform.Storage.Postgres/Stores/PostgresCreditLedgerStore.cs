using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// Postgres-backed <see cref="ICreditLedgerStore"/>: the append-only signed AI-credit ledger
/// (<c>ai_credit_ledger</c>) plus its O(1) balance projection (<c>tenant_credit_balance</c>), migration 012.
/// The request-path balance read is a primary-key lookup of the projection — never a <c>SUM</c> over the
/// ledger. Grants apply unconditionally and are idempotent on <c>period_key</c> (subscription) or
/// <c>external_ref</c> (top-up) via the partial unique indexes + <c>ON CONFLICT DO NOTHING</c>; debits are
/// applied by a single guarded projection <c>UPDATE … WHERE balance &gt;= @amount</c> in the same
/// transaction as the ledger row, so the balance can never go negative even under concurrency. The metered
/// debit (<see cref="PostMeteredDebitAsync"/>) splits consumption into a covered prepaid draw plus a billable
/// <c>PostPaid</c> tail (ADR-0033 addendum, Model C). See ADR-0033 / the credit-ledger-substrate spec delta.
/// Change (b) (credit-ledger-cutover) wires the runtime call-sites onto this store behind default-off flags.
/// </summary>
internal sealed class PostgresCreditLedgerStore : ICreditLedgerStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCreditLedgerStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<decimal> GetBalanceAsync(TenantId tenantId, CancellationToken ct)
    {
        // O(1) primary-key lookup of the projection. NUMERIC(18,6) boxes as decimal; absent row → 0.
        return await _dataSource.ExecuteScalarAsync<decimal>(
            "SELECT balance FROM tenant_credit_balance WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            ct);
    }

    public async Task PostGrantAsync(CreditLedgerEntry grant, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Append the ledger grant row. ON CONFLICT DO NOTHING makes a duplicate subscription
        // (tenant_id, period_key, entry_type) or top-up (tenant_id, external_ref) grant a no-op — the
        // partial unique indexes (migration 012) are the conflict arbiters. Grants with neither key never
        // collide and always insert.
        var inserted = await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, @PeriodKey, @ExternalRef, @ExpiresAt, @UsageRecordId, @CreatedAt) " +
            "ON CONFLICT DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", grant.EntryId.Value));
                p.Add(new NpgsqlParameter("TenantId", grant.TenantId.Value));
                p.Add(new NpgsqlParameter("EntryType", (short)grant.EntryType));
                p.Add(new NpgsqlParameter("Source", (short)grant.Source));
                p.Add(new NpgsqlParameter("Amount", grant.Amount));
                p.Add(new NpgsqlParameter("PeriodKey", NpgsqlDbType.Text) { Value = (object?)grant.PeriodKey ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ExternalRef", NpgsqlDbType.Text) { Value = (object?)grant.ExternalRef ?? DBNull.Value });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = (object?)grant.ExpiresAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("UsageRecordId", NpgsqlDbType.Text) { Value = (object?)grant.UsageRecordId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", grant.CreatedAt));
            },
            tx, ct);

        // Only credit the projection when the ledger row was actually inserted — a deduplicated grant
        // neither double-inserts nor double-credits.
        if (inserted == 1)
        {
            await conn.ExecuteAsync(
                "INSERT INTO tenant_credit_balance (tenant_id, balance, version, updated_at) " +
                "VALUES (@TenantId, @Amount, 1, @Now) " +
                "ON CONFLICT (tenant_id) DO UPDATE SET " +
                "balance = tenant_credit_balance.balance + @Amount, " +
                "version = tenant_credit_balance.version + 1, " +
                "updated_at = @Now",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", grant.TenantId.Value));
                    p.Add(new NpgsqlParameter("Amount", grant.Amount));
                    p.Add(new NpgsqlParameter("Now", grant.CreatedAt));
                },
                tx, ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<CreditDebitResult> TryPostDebitAsync(TenantId tenantId, decimal amount, CreditSource source, string? usageRecordId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Guarded decrement: the WHERE balance >= @Amount predicate is the atomic insufficiency check.
        // Under concurrent debits at most one transaction can satisfy the predicate for the last lot, so
        // the balance can never go negative.
        var affected = await conn.ExecuteAsync(
            "UPDATE tenant_credit_balance SET " +
            "balance = balance - @Amount, version = version + 1, updated_at = @Now " +
            "WHERE tenant_id = @TenantId AND balance >= @Amount",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Amount", amount));
                p.Add(new NpgsqlParameter("Now", now));
            },
            tx, ct);

        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            return CreditDebitResult.RejectedInsufficientBalance;
        }

        // Append the negative-amount ledger debit row in the SAME transaction as the projection update. The
        // row records the lot it drew from (@Source) — never hard-coded PostPaid.
        await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, NULL, NULL, NULL, @UsageRecordId, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", EntityId.New().Value));
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("EntryType", (short)CreditEntryType.Debit));
                p.Add(new NpgsqlParameter("Source", (short)source));
                p.Add(new NpgsqlParameter("Amount", -amount));
                p.Add(new NpgsqlParameter("UsageRecordId", NpgsqlDbType.Text) { Value = (object?)usageRecordId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", now));
            },
            tx, ct);

        // Read the post-debit balance within the transaction (the just-applied UPDATE is visible).
        var newBalance = await ReadBalanceAsync(conn, tx, tenantId, ct);

        await tx.CommitAsync(ct);
        return CreditDebitResult.Posted(newBalance);
    }

    public async Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, CreditSource coveredSource, string? usageRecordId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the projection row (if any) for the duration of the transaction so a concurrent metered debit
        // cannot read a stale balance and over-draw the prepaid lot. An absent row reads as balance 0.
        var balance = await ReadBalanceForUpdateAsync(conn, tx, tenantId, ct);

        var covered = Math.Min(balance, debit);
        var tail = debit - covered;

        if (covered > 0m)
        {
            // Guarded decrement of the prepaid stock. FOR UPDATE already serialises this; the WHERE guard is
            // belt-and-suspenders so the projection can never go negative even if the lock were ever lost.
            await conn.ExecuteAsync(
                "UPDATE tenant_credit_balance SET " +
                "balance = balance - @Covered, version = version + 1, updated_at = @Now " +
                "WHERE tenant_id = @TenantId AND balance >= @Covered",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("Covered", covered));
                    p.Add(new NpgsqlParameter("Now", now));
                },
                tx, ct);

            // Covered debit row records the lot it drew from (@CoveredSource), -covered amount.
            await InsertDebitRowAsync(conn, tx, tenantId, coveredSource, -covered, usageRecordId, now, ct);
        }

        if (tail > 0m)
        {
            // Unconditional billable PostPaid tail — does NOT touch the projection (it stays floored at 0).
            await InsertDebitRowAsync(conn, tx, tenantId, CreditSource.PostPaid, -tail, usageRecordId, now, ct);
        }

        // Re-read the post-debit projection balance within the transaction.
        var newBalance = await ReadBalanceAsync(conn, tx, tenantId, ct);

        await tx.CommitAsync(ct);
        return new MeteredDebitResult(newBalance, covered, tail);
    }

    public async Task<decimal> GetPostPaidDebitsTotalAsync(TenantId tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        // PostPaid debit amounts are negative; negate the SUM to surface the positive customer-owed overage.
        return await _dataSource.ExecuteScalarAsync<decimal?>(
            "SELECT COALESCE(-SUM(amount), 0) FROM ai_credit_ledger " +
            "WHERE tenant_id = @TenantId AND source = @PostPaid AND entry_type = @Debit " +
            "AND created_at >= @Start AND created_at < @End",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("PostPaid", NpgsqlDbType.Smallint) { Value = (short)CreditSource.PostPaid });
                p.Add(new NpgsqlParameter("Debit", NpgsqlDbType.Smallint) { Value = (short)CreditEntryType.Debit });
                p.Add(new NpgsqlParameter("Start", NpgsqlDbType.TimestampTz) { Value = periodStart });
                p.Add(new NpgsqlParameter("End", NpgsqlDbType.TimestampTz) { Value = periodEnd });
            },
            ct) ?? 0m;
    }

    private static async Task InsertDebitRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TenantId tenantId,
        CreditSource source,
        decimal signedAmount,
        string? usageRecordId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, @PeriodKey, @ExternalRef, @ExpiresAt, @UsageRecordId, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", NpgsqlDbType.Text) { Value = EntityId.New().Value });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("EntryType", NpgsqlDbType.Smallint) { Value = (short)CreditEntryType.Debit });
                p.Add(new NpgsqlParameter("Source", NpgsqlDbType.Smallint) { Value = (short)source });
                p.Add(new NpgsqlParameter("Amount", NpgsqlDbType.Numeric) { Value = signedAmount });
                p.Add(new NpgsqlParameter("PeriodKey", NpgsqlDbType.Text) { Value = DBNull.Value });
                p.Add(new NpgsqlParameter("ExternalRef", NpgsqlDbType.Text) { Value = DBNull.Value });
                p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
                p.Add(new NpgsqlParameter("UsageRecordId", NpgsqlDbType.Text) { Value = (object?)usageRecordId ?? DBNull.Value });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = now });
            },
            tx, ct);
    }

    public async Task<IReadOnlyList<CreditLedgerEntry>> GetEntriesAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at " +
            "FROM ai_credit_ledger WHERE tenant_id = @TenantId " +
            "ORDER BY created_at DESC, entry_id DESC LIMIT @Limit OFFSET @Offset",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Limit", pageSize));
                p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize));
            },
            LedgerRow.Map, ct);

        return rows.Select(r => r.ToEntry()).ToList();
    }

    private static async Task<decimal> ReadBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantId tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT balance FROM tenant_credit_balance WHERE tenant_id = @TenantId", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId.Value));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0m : (decimal)result;
    }

    private static async Task<decimal> ReadBalanceForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantId tenantId, CancellationToken ct)
    {
        // FOR UPDATE row-locks the projection cell so a concurrent metered debit serialises behind this one.
        // An absent projection row reads as balance 0 (and nothing to lock — the covered draw is then 0).
        await using var cmd = new NpgsqlCommand(
            "SELECT balance FROM tenant_credit_balance WHERE tenant_id = @TenantId FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId.Value));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0m : (decimal)result;
    }

    private sealed class LedgerRow
    {
        public string entry_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public short entry_type { get; init; }
        public short source { get; init; }
        public decimal amount { get; init; }
        public string? period_key { get; init; }
        public string? external_ref { get; init; }
        public DateTime? expires_at { get; init; }
        public string? usage_record_id { get; init; }
        public DateTime created_at { get; init; }

        public static LedgerRow Map(NpgsqlDataReader r) => new()
        {
            entry_id = r.GetString("entry_id"),
            tenant_id = r.GetString("tenant_id"),
            entry_type = r.GetInt16("entry_type"),
            source = r.GetInt16("source"),
            amount = r.GetDecimal("amount"),
            period_key = r.GetStringOrNull("period_key"),
            external_ref = r.GetStringOrNull("external_ref"),
            expires_at = r.GetDateTimeOrNull("expires_at"),
            usage_record_id = r.GetStringOrNull("usage_record_id"),
            created_at = r.GetDateTime("created_at"),
        };

        public CreditLedgerEntry ToEntry() => new()
        {
            EntryId = EntityId.From(entry_id),
            TenantId = new TenantId(tenant_id),
            EntryType = (CreditEntryType)entry_type,
            Source = (CreditSource)source,
            Amount = amount,
            PeriodKey = period_key,
            ExternalRef = external_ref,
            ExpiresAt = expires_at,
            UsageRecordId = usage_record_id,
            CreatedAt = created_at,
        };
    }
}
