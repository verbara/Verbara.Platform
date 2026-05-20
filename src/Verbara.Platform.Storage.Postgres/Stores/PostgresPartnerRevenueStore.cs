using Npgsql;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

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
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            $"SELECT {SelectColumns} FROM partner_revenue " +
            "WHERE partner_tenant_id = @PartnerId AND invoice_id = @InvoiceId",
            p => { p.Add(new NpgsqlParameter("PartnerId", partnerId.Value)); p.Add(new NpgsqlParameter("InvoiceId", invoiceId.Value)); },
            RevenueRow.Map, ct);

        return row?.ToRecord();
    }

    public async ValueTask<IReadOnlyList<PartnerRevenueRecord>> ListAsync(
        TenantId partnerId,
        DateTimeOffset? from,
        DateTimeOffset? until,
        CancellationToken ct = default)
    {
        List<RevenueRow> rows;

        if (from.HasValue && until.HasValue)
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM partner_revenue " +
                "WHERE partner_tenant_id = @PartnerId AND period_end >= @From AND period_start <= @Until " +
                "ORDER BY period_start DESC",
                p =>
                {
                    p.Add(new NpgsqlParameter("PartnerId", partnerId.Value));
                    p.Add(new NpgsqlParameter("From",      from.Value.UtcDateTime));
                    p.Add(new NpgsqlParameter("Until",     until.Value.UtcDateTime));
                },
                RevenueRow.Map, ct);
        }
        else if (from.HasValue)
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM partner_revenue " +
                "WHERE partner_tenant_id = @PartnerId AND period_end >= @From " +
                "ORDER BY period_start DESC",
                p => { p.Add(new NpgsqlParameter("PartnerId", partnerId.Value)); p.Add(new NpgsqlParameter("From", from.Value.UtcDateTime)); },
                RevenueRow.Map, ct);
        }
        else if (until.HasValue)
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM partner_revenue " +
                "WHERE partner_tenant_id = @PartnerId AND period_start <= @Until " +
                "ORDER BY period_start DESC",
                p => { p.Add(new NpgsqlParameter("PartnerId", partnerId.Value)); p.Add(new NpgsqlParameter("Until", until.Value.UtcDateTime)); },
                RevenueRow.Map, ct);
        }
        else
        {
            rows = await _dataSource.QueryListAsync(
                $"SELECT {SelectColumns} FROM partner_revenue " +
                "WHERE partner_tenant_id = @PartnerId " +
                "ORDER BY period_start DESC",
                p => p.Add(new NpgsqlParameter("PartnerId", partnerId.Value)),
                RevenueRow.Map, ct);
        }

        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async ValueTask UpsertAsync(PartnerRevenueRecord record, CancellationToken ct = default)
    {
        await _dataSource.ExecuteAsync(
            "INSERT INTO partner_revenue " +
            "(revenue_id, partner_tenant_id, customer_tenant_id, invoice_id, " +
            " gross_amount, platform_cost, partner_margin, period_start, period_end, created_at) " +
            "VALUES (@RevenueId, @PartnerTenantId, @CustomerTenantId, @InvoiceId, " +
            "        @GrossAmount, @PlatformCost, @PartnerMargin, @PeriodStart, @PeriodEnd, @CreatedAt) " +
            "ON CONFLICT (revenue_id) DO UPDATE SET " +
            "  gross_amount = EXCLUDED.gross_amount, " +
            "  platform_cost = EXCLUDED.platform_cost, " +
            "  partner_margin = EXCLUDED.partner_margin",
            p =>
            {
                p.Add(new NpgsqlParameter("RevenueId",        record.RevenueId.Value));
                p.Add(new NpgsqlParameter("PartnerTenantId",  record.PartnerTenantId.Value));
                p.Add(new NpgsqlParameter("CustomerTenantId", record.CustomerTenantId.Value));
                p.Add(new NpgsqlParameter("InvoiceId",        record.InvoiceId.Value));
                p.Add(new NpgsqlParameter("GrossAmount",      record.GrossAmount));
                p.Add(new NpgsqlParameter("PlatformCost",     record.PlatformCost));
                p.Add(new NpgsqlParameter("PartnerMargin",    record.PartnerMargin));
                p.Add(new NpgsqlParameter("PeriodStart",      record.PeriodStart.UtcDateTime));
                p.Add(new NpgsqlParameter("PeriodEnd",        record.PeriodEnd.UtcDateTime));
                p.Add(new NpgsqlParameter("CreatedAt",        record.CreatedAt.UtcDateTime));
            },
            ct);
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

        public static RevenueRow Map(NpgsqlDataReader r) => new()
        {
            revenue_id        = r.GetString("revenue_id"),
            partner_tenant_id = r.GetString("partner_tenant_id"),
            customer_tenant_id = r.GetString("customer_tenant_id"),
            invoice_id        = r.GetString("invoice_id"),
            gross_amount      = r.GetDecimal("gross_amount"),
            platform_cost     = r.GetDecimal("platform_cost"),
            partner_margin    = r.GetDecimal("partner_margin"),
            period_start      = r.GetDateTime("period_start"),
            period_end        = r.GetDateTime("period_end"),
            created_at        = r.GetDateTime("created_at"),
        };

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
