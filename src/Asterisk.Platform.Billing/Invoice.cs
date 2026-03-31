using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

public sealed class Invoice : ITenantScoped
{
    public required EntityId InvoiceId { get; init; }
    public required TenantId TenantId { get; init; }
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<InvoiceLineItem> LineItems { get; init; }
    public required decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public required DateTimeOffset GeneratedAt { get; init; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    Void,
}

public sealed class InvoiceLineItem
{
    public required UsageType UsageType { get; init; }
    public required string Description { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal Amount { get; init; }
    public decimal IncludedQuantity { get; init; }
    public decimal OverageQuantity { get; init; }
}
