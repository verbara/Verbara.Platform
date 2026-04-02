using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO invoices (invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at) " +
            "VALUES (@InvoiceId, @TenantId, @PeriodStart, @PeriodEnd, @Currency, @LineItems::jsonb, @Subtotal, @Tax, @Total, @Status, @GeneratedAt, @IssuedAt, @PaidAt)",
            new
            {
                InvoiceId = invoice.InvoiceId.Value,
                TenantId = invoice.TenantId.Value,
                invoice.PeriodStart,
                invoice.PeriodEnd,
                invoice.Currency,
                LineItems = lineItemsJson,
                invoice.Subtotal,
                invoice.Tax,
                invoice.Total,
                Status = (short)invoice.Status,
                invoice.GeneratedAt,
                invoice.IssuedAt,
                invoice.PaidAt,
            });
    }

    public async Task<Invoice?> GetByIdAsync(TenantId tenantId, EntityId invoiceId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InvoiceRow?>(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            new { TenantId = tenantId.Value, InvoiceId = invoiceId.Value });

        return row?.ToInvoice();
    }

    public async Task<IReadOnlyList<Invoice>> ListAsync(TenantId tenantId, int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<InvoiceRow>(
            "SELECT invoice_id, tenant_id, period_start, period_end, currency, line_items, subtotal, tax, total, status, generated_at, issued_at, paid_at " +
            "FROM invoices WHERE tenant_id = @TenantId ORDER BY period_start DESC " +
            "LIMIT @PageSize OFFSET @Offset",
            new { TenantId = tenantId.Value, PageSize = pageSize, Offset = (page - 1) * pageSize });

        return rows.Select(r => r.ToInvoice()).ToList();
    }

    public async Task UpdateStatusAsync(TenantId tenantId, EntityId invoiceId, InvoiceStatus status, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE invoices SET status = @Status, " +
            "issued_at = CASE WHEN @Status = 1 THEN NOW() ELSE issued_at END, " +
            "paid_at = CASE WHEN @Status = 2 THEN NOW() ELSE paid_at END " +
            "WHERE tenant_id = @TenantId AND invoice_id = @InvoiceId",
            new { TenantId = tenantId.Value, InvoiceId = invoiceId.Value, Status = (short)status });
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
