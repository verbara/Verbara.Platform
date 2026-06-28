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
/// debit (<see cref="PostMeteredDebitAsync"/>) is a FIFO multi-source lot allocation (ADR-0033 / the
/// 2026-06-28 (c2) addendum); the covered portion is drawn across open lots in priority order, the uncovered
/// remainder is a single PostPaid tail. See ADR-0033 / the credit-ledger-substrate spec delta.
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

            // Mint one credit_lot row mirroring the grant (one lot per grant). The per-tenant lot_seq is the
            // deterministic monotonic FIFO tiebreak — bumped in the SAME transaction so it serialises behind any
            // concurrent grant for this tenant. A brand-new tenant's first lot = 0; a migration-013-seeded tenant
            // (credit_lot_seq.next_seq was set to 1, the miglot has lot_seq 0) gets its first real lot >= 2 —
            // strictly past the migration lot, keeping the total FIFO order intact. Only reached when inserted == 1
            // so a deduplicated grant never mints a second lot.
            var lotSeq = await NextLotSeqAsync(conn, tx, grant.TenantId, ct);

            await conn.ExecuteAsync(
                "INSERT INTO credit_lot " +
                "(lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq) " +
                "VALUES (@LotId, @TenantId, @Source, @Original, @Remaining, @ExpiresAt, @GrantedAt, @LotSeq)",
                p =>
                {
                    p.Add(new NpgsqlParameter("LotId", NpgsqlDbType.Text) { Value = grant.EntryId.Value });
                    p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = grant.TenantId.Value });
                    p.Add(new NpgsqlParameter("Source", NpgsqlDbType.Smallint) { Value = (short)grant.Source });
                    p.Add(new NpgsqlParameter("Original", NpgsqlDbType.Numeric) { Value = grant.Amount });
                    p.Add(new NpgsqlParameter("Remaining", NpgsqlDbType.Numeric) { Value = grant.Amount });
                    p.Add(new NpgsqlParameter("ExpiresAt", NpgsqlDbType.TimestampTz) { Value = (object?)grant.ExpiresAt ?? DBNull.Value });
                    p.Add(new NpgsqlParameter("GrantedAt", NpgsqlDbType.TimestampTz) { Value = grant.CreatedAt });
                    p.Add(new NpgsqlParameter("LotSeq", NpgsqlDbType.Bigint) { Value = lotSeq });
                },
                tx, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<long> NextLotSeqAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TenantId tenantId, CancellationToken ct)
    {
        // Bump the per-tenant lot sequence in the SAME transaction and return the value to use as lot_seq. A
        // brand-new tenant inserts next_seq = 0 (its first lot uses 0); an existing row is bumped by 1 and the
        // post-increment value returned. RETURNING reads the value atomically with the bump.
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO credit_lot_seq (tenant_id, next_seq) VALUES (@TenantId, 0) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET next_seq = credit_lot_seq.next_seq + 1 " +
            "RETURNING next_seq",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (long)result!;
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

    public async Task<MeteredDebitResult> PostMeteredDebitAsync(TenantId tenantId, decimal debit, string? usageRecordId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // LOCK ORDER (the deadlock-avoidance contract): the projection row FIRST, then the open lots in the
        // total FIFO order. Locking the projection cell serialises any concurrent metered debit for this tenant
        // behind this one; an absent row reads as balance 0 (and there are no lots to draw).
        var balance = await ReadBalanceForUpdateAsync(conn, tx, tenantId, ct);

        // Select + lock the open, non-expired lots in the provably-total consumption order
        // (billable_priority ASC, expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC). The CASE encodes
        // BillablePriority.Of — Promo(source 2)→0, Partner(source 3)→1, else(Subscription 0/TopUp 1)→2 — NEVER
        // ORDER BY source. Read every row into a local list BEFORE mutating (a reader can't outlive its own
        // UPDATEs on the same connection).
        var lots = await LockOpenLotsAsync(conn, tx, tenantId, now, ct);

        var outstanding = debit;
        var coveredTotal = 0m;

        foreach (var lot in lots)
        {
            if (outstanding <= 0m)
                break;

            var draw = Math.Min(lot.Remaining, outstanding);
            if (draw <= 0m)
                continue;

            // Guarded per-lot decrement (shared with the back-fill draw); then one source-tagged covered debit
            // row (the lot's OWN source) + its internal credit_allocation row linked to that row's entry_id.
            await DecrementLotGuardedAsync(conn, tx, lot.LotId, draw, ct);
            var debitEntryId = await InsertDebitRowAsync(conn, tx, tenantId, lot.Source, -draw, usageRecordId, now, ct);
            await InsertAllocationRowAsync(conn, tx, debitEntryId, lot.LotId, lot.Source, draw, now, ct);

            outstanding -= draw;
            coveredTotal += draw;
        }

        if (coveredTotal > 0m)
        {
            // ONE guarded projection decrement by the total covered. By the invariant Σ(open lot.remaining) ==
            // balance, coveredTotal == min(debit, balance) <= balance, so the WHERE balance >= @Covered guard
            // always passes here.
            await conn.ExecuteAsync(
                "UPDATE tenant_credit_balance SET " +
                "balance = balance - @Covered, version = version + 1, updated_at = @Now " +
                "WHERE tenant_id = @TenantId AND balance >= @Covered",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                    p.Add(new NpgsqlParameter("Covered", coveredTotal));
                    p.Add(new NpgsqlParameter("Now", now));
                },
                tx, ct);
        }

        var tail = debit - coveredTotal;

        if (tail > 0m)
        {
            // Exactly ONE unconditional billable PostPaid tail row — never a lot, never split, no allocation. Does
            // NOT touch the projection (it stays floored at 0).
            await InsertDebitRowAsync(conn, tx, tenantId, CreditSource.PostPaid, -tail, usageRecordId, now, ct);
        }

        // Re-read the post-debit projection balance within the transaction.
        var newBalance = await ReadBalanceAsync(conn, tx, tenantId, ct);

        await tx.CommitAsync(ct);
        return new MeteredDebitResult(newBalance, coveredTotal, tail);
    }

    private static async Task<IReadOnlyList<CreditLot>> LockOpenLotsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TenantId tenantId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // SELECT … FOR UPDATE in the EXACT total FIFO/lock order. The CASE mirrors BillablePriority.Of (Promo
        // source=2 → 0, Partner source=3 → 1, Subscription source=0 / TopUp source=1 → 2). Read every locked row
        // into a list before the caller mutates any of them.
        await using var cmd = new NpgsqlCommand(
            "SELECT lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq " +
            "FROM credit_lot " +
            "WHERE tenant_id = @TenantId AND remaining > 0 AND (expires_at IS NULL OR expires_at > @Now) " +
            "ORDER BY (CASE source WHEN 2 THEN 0 WHEN 3 THEN 1 ELSE 2 END) ASC, " +
            "expires_at ASC NULLS LAST, granted_at ASC, lot_seq ASC " +
            "FOR UPDATE",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
        cmd.Parameters.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now });

        var lots = new List<CreditLot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            lots.Add(MapLot(reader));

        return lots;
    }

    private static async Task DecrementLotGuardedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string lotId,
        decimal draw,
        CancellationToken ct)
    {
        // Guarded per-lot decrement shared by the metered-debit and back-fill FIFO walks. FOR UPDATE already
        // serialises this; the WHERE remaining >= @Draw guard is belt-and-suspenders so a lot can never be
        // overdrawn even if the lock were ever lost. Asserting exactly one row is affected catches any drift
        // between the locked snapshot and the live row.
        var affected = await conn.ExecuteAsync(
            "UPDATE credit_lot SET remaining = remaining - @Draw WHERE lot_id = @LotId AND remaining >= @Draw",
            p =>
            {
                p.Add(new NpgsqlParameter("Draw", NpgsqlDbType.Numeric) { Value = draw });
                p.Add(new NpgsqlParameter("LotId", NpgsqlDbType.Text) { Value = lotId });
            },
            tx, ct);

        if (affected != 1)
            throw new InvalidOperationException(
                $"Guarded credit_lot decrement affected {affected} rows for lot '{lotId}' (expected 1 under FOR UPDATE).");
    }

    public async Task PostBackfillConsumptionAsync(TenantId tenantId, decimal consumed, string periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKey);

        var now = DateTimeOffset.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // A non-positive consumption seeds nothing — commit the empty transaction and return.
        if (consumed <= 0m)
        {
            await tx.CommitAsync(ct);
            return;
        }

        // Lock the projection cell for the duration of the transaction so the covered draw reads a stable
        // balance (an absent row reads as 0). Mirrors PostMeteredDebitAsync.
        var balance = await ReadBalanceForUpdateAsync(conn, tx, tenantId, ct);

        var covered = Math.Min(balance, consumed);
        var tail = consumed - covered;

        // The covered (Subscription) debit row carries the idempotency marker external_ref = "backfill:{periodKey}".
        // ON CONFLICT DO NOTHING against the migration-012 partial unique index (tenant_id, external_ref) is the
        // arbiter: a second back-fill of the same period inserts 0 rows and the whole operation short-circuits. The
        // returned entry_id is the link target for the FIFO lot-draw's credit_allocation rows below.
        var externalRef = $"backfill:{periodKey}";
        var (inserted, markerEntryId) = await InsertBackfillCoveredRowAsync(conn, tx, tenantId, -covered, externalRef, now, ct);

        if (inserted == 0)
        {
            // Already back-filled for this period — a complete no-op (no balance decrement, no lot draw, no PostPaid tail).
            await tx.CommitAsync(ct);
            return;
        }

        if (covered > 0m)
        {
            // Lot-aware covered draw (the (c2) addendum): draw `covered` from the open lots in the SAME total FIFO
            // order as PostMeteredDebitAsync (priority → expires_at NULLS LAST → granted_at → lot_seq), decrementing
            // each lot's remaining and writing one credit_allocation row per drawn lot ALL linked to the single
            // marker covered row's entry_id (so Σ allocation.amount == covered and the lots move down by exactly the
            // projection decrement). By the invariant Σ(open lot.remaining) == balance and covered = min(balance,
            // consumed), the walk always fully covers. The marker stays a single Subscription-tagged row — only the
            // projection-coupled lots + allocations are added (the lots are NOT split into per-lot debit rows).
            var lots = await LockOpenLotsAsync(conn, tx, tenantId, now, ct);
            var outstanding = covered;

            foreach (var lot in lots)
            {
                if (outstanding <= 0m)
                    break;

                var draw = Math.Min(lot.Remaining, outstanding);
                if (draw <= 0m)
                    continue;

                await DecrementLotGuardedAsync(conn, tx, lot.LotId, draw, ct);
                await InsertAllocationRowAsync(conn, tx, markerEntryId, lot.LotId, lot.Source, draw, now, ct);

                outstanding -= draw;
            }

            // Guarded decrement of the prepaid stock drawn down by this period's covered consumption. FOR UPDATE
            // already serialises this; the WHERE guard keeps the projection from ever going negative. Decrements by
            // exactly Σ(lot draws) == covered, keeping Σ(open lot.remaining) == balance.
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
        }

        if (tail > 0m)
        {
            // Unconditional billable PostPaid tail (external_ref NULL — the covered marker alone guards the whole
            // operation, so this is written exactly once). Does NOT touch the projection.
            await InsertDebitRowAsync(conn, tx, tenantId, CreditSource.PostPaid, -tail, usageRecordId: null, now, ct);
        }

        await tx.CommitAsync(ct);
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

    public async Task<IReadOnlyList<SourceRemaining>> GetRemainingBySourceAsync(TenantId tenantId, DateTimeOffset now, CancellationToken ct)
    {
        // Sum open (remaining > 0), non-expired (expires_at IS NULL OR > @Now) lot remaining per source. PostPaid
        // is never a lot so it never appears; expired Promo lots pending reclaim are excluded. The Σ over the
        // returned rows equals GetBalanceAsync (Σ(open non-expired lot.remaining) == projection.balance).
        var rows = await _dataSource.QueryListAsync(
            "SELECT source, SUM(remaining) AS remaining FROM credit_lot " +
            "WHERE tenant_id = @TenantId AND remaining > 0 AND (expires_at IS NULL OR expires_at > @Now) " +
            "GROUP BY source",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now });
            },
            static r => new SourceRemaining((CreditSource)r.GetInt16("source"), r.GetDecimal("remaining")),
            ct);

        return rows;
    }

    public async Task<IReadOnlyList<CreditLot>> GetLotsAsync(TenantId tenantId, CancellationToken ct)
    {
        // Test/diagnostic read of the raw lot projection (all lots, including drawn-to-zero and expired).
        var rows = await _dataSource.QueryListAsync(
            "SELECT lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq " +
            "FROM credit_lot WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value }),
            MapLot,
            ct);

        return rows;
    }

    public async Task<IReadOnlyList<CreditLot>> GetExpiredLotsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The reclaim sweeper's cross-tenant work-list — a single SELECT over every non-empty, actually-expired
        // lot (any source carrying an expiry: Promo's operator expiry, the Subscription period-end no-carryover
        // lot). No tenant filter; the sweeper reclaims each in its own transaction.
        var rows = await _dataSource.QueryListAsync(
            "SELECT lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq " +
            "FROM credit_lot " +
            "WHERE remaining > 0 AND expires_at IS NOT NULL AND expires_at <= @Now",
            p => p.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now }),
            MapLot,
            ct);

        return rows;
    }

    public async Task<decimal> ReclaimExpiredLotAsync(TenantId tenantId, string lotId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lotId);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // (1) LOCK ORDER (the deadlock-avoidance contract): the projection row FIRST (FOR UPDATE), exactly as the
        // metered debit + back-fill. Then read the lot FOR UPDATE.
        await ReadBalanceForUpdateAsync(conn, tx, tenantId, ct);

        var lot = await ReadLotForUpdateAsync(conn, tx, lotId, ct);

        // (2) If the lot is missing, already drained, or not actually expired (null OR > now), commit empty + 0.
        if (lot is null
            || lot.Remaining <= 0m
            || lot.ExpiresAt is not { } exp
            || exp > now)
        {
            await tx.CommitAsync(ct);
            return 0m;
        }

        // (3) Offsetting debit tagged the lot's OWN source, amount = -live remaining (NOT original), carrying the
        // idempotency marker external_ref = "lot-expiry:{lotId}". ON CONFLICT DO NOTHING against the migration-012
        // partial unique index (tenant_id, external_ref) is the arbiter: a re-tick inserts 0 rows.
        var reclaimed = lot.Remaining;
        var externalRef = $"lot-expiry:{lotId}";
        var inserted = await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, NULL, @ExternalRef, NULL, NULL, @CreatedAt) " +
            "ON CONFLICT DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", NpgsqlDbType.Text) { Value = EntityId.New().Value });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("EntryType", NpgsqlDbType.Smallint) { Value = (short)CreditEntryType.Debit });
                p.Add(new NpgsqlParameter("Source", NpgsqlDbType.Smallint) { Value = (short)lot.Source });
                p.Add(new NpgsqlParameter("Amount", NpgsqlDbType.Numeric) { Value = -reclaimed });
                p.Add(new NpgsqlParameter("ExternalRef", NpgsqlDbType.Text) { Value = externalRef });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = now });
            },
            tx, ct);

        // (5) Already reclaimed (inserted == 0): a complete no-op — does NOT re-decrement the projection or
        // re-zero the lot. Commit + return 0.
        if (inserted == 0)
        {
            await tx.CommitAsync(ct);
            return 0m;
        }

        // (4) Marker newly inserted ⇒ guarded projection decrement by `reclaimed` + zero the lot, both in the same
        // transaction. By the invariant Σ(open non-expired lot.remaining) == balance the WHERE balance >= @Remaining
        // guard always passes.
        await conn.ExecuteAsync(
            "UPDATE tenant_credit_balance SET " +
            "balance = balance - @Remaining, version = version + 1, updated_at = @Now " +
            "WHERE tenant_id = @TenantId AND balance >= @Remaining",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId.Value));
                p.Add(new NpgsqlParameter("Remaining", reclaimed));
                p.Add(new NpgsqlParameter("Now", now));
            },
            tx, ct);

        await conn.ExecuteAsync(
            "UPDATE credit_lot SET remaining = 0 WHERE lot_id = @LotId",
            p => p.Add(new NpgsqlParameter("LotId", NpgsqlDbType.Text) { Value = lotId }),
            tx, ct);

        await tx.CommitAsync(ct);
        return reclaimed;
    }

    private static async Task<CreditLot?> ReadLotForUpdateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string lotId,
        CancellationToken ct)
    {
        // Row-lock the lot for the duration of the reclaim transaction so a concurrent metered debit / reclaim
        // serialises behind this one. Returns null when the lot does not exist.
        await using var cmd = new NpgsqlCommand(
            "SELECT lot_id, tenant_id, source, original, remaining, expires_at, granted_at, lot_seq " +
            "FROM credit_lot WHERE lot_id = @LotId FOR UPDATE",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("LotId", NpgsqlDbType.Text) { Value = lotId });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return MapLot(reader);
    }

    private static CreditLot MapLot(NpgsqlDataReader r) => new()
    {
        LotId = r.GetString("lot_id"),
        TenantId = new TenantId(r.GetString("tenant_id")),
        Source = (CreditSource)r.GetInt16("source"),
        Original = r.GetDecimal("original"),
        Remaining = r.GetDecimal("remaining"),
        ExpiresAt = r.GetDateTimeOffsetOrNull("expires_at"),
        GrantedAt = r.GetDateTimeOffset("granted_at"),
        LotSeq = r.GetInt64("lot_seq"),
    };

    private static async Task<string> InsertDebitRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TenantId tenantId,
        CreditSource source,
        decimal signedAmount,
        string? usageRecordId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Generate the entry_id here and RETURN it so the FIFO walk can link the matching credit_allocation row.
        var entryId = EntityId.New().Value;
        await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, @PeriodKey, @ExternalRef, @ExpiresAt, @UsageRecordId, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", NpgsqlDbType.Text) { Value = entryId });
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
        return entryId;
    }

    private static async Task InsertAllocationRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string debitEntryId,
        string lotId,
        CreditSource source,
        decimal amount,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // One internal credit_allocation row per (covered debit × drawn lot). Amount is positive (the draw). This
        // table is invisible to GetEntries(Count)Async / GetPostPaidDebitsTotalAsync (scoped to ai_credit_ledger).
        await conn.ExecuteAsync(
            "INSERT INTO credit_allocation " +
            "(allocation_id, debit_entry_id, lot_id, source, amount, created_at) " +
            "VALUES (@AllocationId, @DebitEntryId, @LotId, @Source, @Amount, @CreatedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("AllocationId", NpgsqlDbType.Text) { Value = EntityId.New().Value });
                p.Add(new NpgsqlParameter("DebitEntryId", NpgsqlDbType.Text) { Value = debitEntryId });
                p.Add(new NpgsqlParameter("LotId", NpgsqlDbType.Text) { Value = lotId });
                p.Add(new NpgsqlParameter("Source", NpgsqlDbType.Smallint) { Value = (short)source });
                p.Add(new NpgsqlParameter("Amount", NpgsqlDbType.Numeric) { Value = amount });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = now });
            },
            tx, ct);
    }

    private static async Task<(int Inserted, string EntryId)> InsertBackfillCoveredRowAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TenantId tenantId,
        decimal signedAmount,
        string externalRef,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Covered (Subscription) back-fill debit carrying the idempotency marker external_ref. ON CONFLICT
        // DO NOTHING against the partial unique index (tenant_id, external_ref) WHERE external_ref IS NOT NULL
        // (migration 012) makes a re-back-fill of the same period a 0-row no-op — the operation's idempotency arbiter.
        // Returns the rows-affected (0 ⇒ duplicate) AND the entry_id, so the caller can link the FIFO lot-draw's
        // credit_allocation rows to this single marker row's entry_id.
        var entryId = EntityId.New().Value;
        var inserted = await conn.ExecuteAsync(
            "INSERT INTO ai_credit_ledger " +
            "(entry_id, tenant_id, entry_type, source, amount, period_key, external_ref, expires_at, usage_record_id, created_at) " +
            "VALUES (@EntryId, @TenantId, @EntryType, @Source, @Amount, NULL, @ExternalRef, NULL, NULL, @CreatedAt) " +
            "ON CONFLICT DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("EntryId", NpgsqlDbType.Text) { Value = entryId });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId.Value });
                p.Add(new NpgsqlParameter("EntryType", NpgsqlDbType.Smallint) { Value = (short)CreditEntryType.Debit });
                p.Add(new NpgsqlParameter("Source", NpgsqlDbType.Smallint) { Value = (short)CreditSource.Subscription });
                p.Add(new NpgsqlParameter("Amount", NpgsqlDbType.Numeric) { Value = signedAmount });
                p.Add(new NpgsqlParameter("ExternalRef", NpgsqlDbType.Text) { Value = externalRef });
                p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = now });
            },
            tx, ct);
        return (inserted, entryId);
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

    public async Task<int> GetEntriesCountAsync(TenantId tenantId, CancellationToken ct)
    {
        // COUNT(*) over the tenant's ledger rows — backs PagedResult.TotalCount. Covered by
        // idx_ai_credit_ledger_tenant_created (tenant_id, created_at). COUNT boxes as bigint → long.
        var count = await _dataSource.ExecuteScalarAsync<long?>(
            "SELECT COUNT(*) FROM ai_credit_ledger WHERE tenant_id = @TenantId",
            p => p.Add(new NpgsqlParameter("TenantId", tenantId.Value)),
            ct) ?? 0L;
        return (int)count;
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
