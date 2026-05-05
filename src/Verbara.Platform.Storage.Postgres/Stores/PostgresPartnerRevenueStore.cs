using Dapper;
using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPartnerRevenueStore : IPartnerRevenueStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPartnerRevenueStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns =
        "revenue_id, partner_tenant_id, customer_tenant_id, invoice_id, " +
        "gross_amount, platform_cost, partner_margin, period_start, period_end, created_at";

    public async ValueTask<PartnerRevenueRecord?> GetByInvoiceAsync(TenantId partnerId, EntityId invoiceId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RevenueRow>(
            $"SELECT {SelectColumns} FROM partner_revenue " +
            "WHERE partner_tenant_id = @PartnerId AND invoice_id = @InvoiceId",
            new { PartnerId = partnerId.Value, InvoiceId = invoiceId.Value });

        return row?.ToRecord();
    }

    public async ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(
        TenantId partnerId,
        DateTimeOffset? from,
        DateTimeOffset? until,
        CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM partner_revenue WHERE partner_tenant_id = @PartnerId";

        if (from.HasValue)
            sql += " AND period_end >= @From";
        if (until.HasValue)
            sql += " AND period_start <= @Until";

        sql += " ORDER BY period_start DESC";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RevenueRow>(sql, new
        {
            PartnerId = partnerId.Value,
            From = from?.UtcDateTime,
            Until = until?.UtcDateTime,
        });

        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO partner_revenue " +
            "(revenue_id, partner_tenant_id, customer_tenant_id, invoice_id, " +
            " gross_amount, platform_cost, partner_margin, period_start, period_end, created_at) " +
            "VALUES (@RevenueId, @PartnerTenantId, @CustomerTenantId, @InvoiceId, " +
            "        @GrossAmount, @PlatformCost, @PartnerMargin, @PeriodStart, @PeriodEnd, @CreatedAt) " +
            "ON CONFLICT (revenue_id) DO UPDATE SET " +
            "  gross_amount = EXCLUDED.gross_amount, " +
            "  platform_cost = EXCLUDED.platform_cost, " +
            "  partner_margin = EXCLUDED.partner_margin",
            new
            {
                RevenueId = record.RevenueId.Value,
                PartnerTenantId = record.PartnerTenantId.Value,
                CustomerTenantId = record.CustomerTenantId.Value,
                InvoiceId = record.InvoiceId.Value,
                record.GrossAmount,
                record.PlatformCost,
                record.PartnerMargin,
                PeriodStart = record.PeriodStart.UtcDateTime,
                PeriodEnd = record.PeriodEnd.UtcDateTime,
                CreatedAt = record.CreatedAt.UtcDateTime,
            });
    }

    private sealed class RevenueRow
    {
        public string revenue_id { get; init; } = null!;
        public string partner_tenant_id { get; init; } = null!;
        public string customer_tenant_id { get; init; } = null!;
        public string invoice_id { get; init; } = null!;
        public decimal gross_amount { get; init; }
        public decimal platform_cost { get; init; }
        public decimal partner_margin { get; init; }
        public DateTime period_start { get; init; }
        public DateTime period_end { get; init; }
        public DateTime created_at { get; init; }

        public PartnerRevenueRecord ToRecord() => new()
        {
            RevenueId = EntityId.From(revenue_id),
            PartnerTenantId = new TenantId(partner_tenant_id),
            CustomerTenantId = new TenantId(customer_tenant_id),
            InvoiceId = EntityId.From(invoice_id),
            GrossAmount = gross_amount,
            PlatformCost = platform_cost,
            PartnerMargin = partner_margin,
            PeriodStart = new DateTimeOffset(period_start, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(period_end, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(created_at, TimeSpan.Zero),
        };
    }
}
