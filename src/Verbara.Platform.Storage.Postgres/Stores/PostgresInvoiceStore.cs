using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Stores;

public sealed class PostgresInvoiceStore : IInvoiceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresInvoiceStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveAsync(Invoice invoice, CancellationToken ct)
    {
        var lineItemsJson = JsonSerializer.Serialize(invoice.LineItems, PostgresJson.Ctx.IReadOnlyListInvoiceLineItem);

        await _dataSource.ExecuteAsync(
            "INSERT INTO invoices (invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at) " +
            "VALUES (@InvoiceId, @TenantId, @PeriodStart, @PeriodEnd, @Currency, @LineItems::jsonb, @Subtotal, @Tax, @Total, @Status, @GeneratedAt, @IssuedAt, @PaidAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("InvoiceId",   invoice.InvoiceId.Value));
                p.Add(new NpgsqlParameter("TenantId",    invoice.TenantId.Value));
                p.Add(new NpgsqlParameter("PeriodStart", invoice.PeriodStart));
                p.Add(new NpgsqlParameter("PeriodEnd",   invoice.PeriodEnd));
                p.Add(new NpgsqlParameter("Currency",    invoice.Currency));
                p.Add(new NpgsqlParameter("LineItems",   lineItemsJson));
                p.Add(new NpgsqlParameter("Subtotal",    invoice.Subtotal));
                p.Add(new NpgsqlParameter("Tax",         invoice.Tax));
                p.Add(new NpgsqlParameter("Total",       invoice.Total));
                p.Add(new NpgsqlParameter("Status",      (short)invoice.Status));
                p.Add(new NpgsqlParameter("GeneratedAt", invoice.GeneratedAt));
                p.Add(new NpgsqlParameter("IssuedAt", NpgsqlDbType.TimestampTz) { Value = (object?)invoice.IssuedAt ?? DBNull.Value });
                p.Add(new NpgsqlParameter("PaidAt", NpgsqlDbType.TimestampTz) { Value = (object?)invoice.PaidAt ?? DBNull.Value });
            },
            ct);
    }

    public async Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct)
    {
        var row = await _dataSource.QuerySingleOrDefaultAsync(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("InvoiceId", invoiceId.Value)); },
            InvoiceRow.Map, ct);

        return row?.ToInvoice();
    }

    public async Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId ORDER BY period_start DESC " +
            "LIMIT @PageSize OFFSET @Offset",
            p => { p.Add(new NpgsqlParameter("TenantId", tenantId.Value)); p.Add(new NpgsqlParameter("PageSize", pageSize)); p.Add(new NpgsqlParameter("Offset", (page - 1) * pageSize)); },
            InvoiceRow.Map, ct);

        return rows.Select(r => r.ToInvoice()).ToList();
    }

    public async Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct)
    {
        await _dataSource.ExecuteAsync(
            "UPDATE invoices SET status = @Status, " +
            "issued_at = CASE WHEN @Status = 1 THEN NOW() ELSE issued_at END, " +
            "paid_at = CASE WHEN @Status = 2 THEN NOW() ELSE paid_at END " +
            "WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId",  tenantId.Value));
                p.Add(new NpgsqlParameter("InvoiceId", invoiceId.Value));
                p.Add(new NpgsqlParameter("Status",    (short)status));
            },
            ct);
    }

    public async Task<IReadOnlyList<Invoice>> ListByStatusAsync(InvoiceStatus status, CancellationToken ct = default)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE status = @Status ORDER BY period_start DESC",
            p => p.Add(new NpgsqlParameter("Status", (short)status)),
            InvoiceRow.Map, ct);

        return rows.Select(r => r.ToInvoice()).ToList();
    }

    private sealed class InvoiceRow
    {
        public string invoice_id { get; init; } = null!;
        public string tenant_id { get; init; } = null!;
        public DateTime period_start { get; init; }
        public DateTime period_end { get; init; }
        public string currency { get; init; } = null!;
        public string line_items { get; init; } = null!;
        public decimal subtotal { get; init; }
        public decimal tax { get; init; }
        public decimal total { get; init; }
        public short status { get; init; }
        public DateTime generated_at { get; init; }
        public DateTime? issued_at { get; init; }
        public DateTime? paid_at { get; init; }

        public static InvoiceRow Map(NpgsqlDataReader r) => new()
        {
            invoice_id   = r.GetString("invoice_id"),
            tenant_id    = r.GetString("tenant_id"),
            period_start = r.GetDateTime("period_start"),
            period_end   = r.GetDateTime("period_end"),
            currency     = r.GetString("currency"),
            line_items   = r.GetString("line_items"),
            subtotal     = r.GetDecimal("subtotal"),
            tax          = r.GetDecimal("tax"),
            total        = r.GetDecimal("total"),
            status       = r.GetInt16("status"),
            generated_at = r.GetDateTime("generated_at"),
            issued_at    = r.GetDateTimeOrNull("issued_at"),
            paid_at      = r.GetDateTimeOrNull("paid_at"),
        };

        public Invoice ToInvoice() => new()
        {
            InvoiceId = EntityId.From(invoice_id),
            TenantId = new TenantId(tenant_id),
            PeriodStart = period_start,
            PeriodEnd = period_end,
            Currency = currency,
            LineItems = JsonSerializer.Deserialize(line_items, PostgresJson.Ctx.IReadOnlyListInvoiceLineItem) ?? [],
            Subtotal = subtotal,
            Tax = tax,
            Total = total,
            Status = (InvoiceStatus)status,
            GeneratedAt = generated_at,
            IssuedAt = issued_at,
            PaidAt = paid_at,
        };
    }
}
