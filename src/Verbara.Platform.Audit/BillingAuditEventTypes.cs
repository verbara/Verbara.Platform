namespace Verbara.Platform.Audit;

/// <summary>
/// Canonical action constants for billing-domain audit entries. Introduced
/// for the <c>PREPUB-2026-05-09-BILL-001</c> remediation: every money-mutating
/// handler in <c>ManagementBillingEndpoints</c> must emit through one of these
/// constants so the audit trail is grep-able and stable. Severity guidance is
/// per-handler (critical for delete; warning for create/update/issue/pay/quota).
/// </summary>
public static class BillingAuditEventTypes
{
    // Rate cards
    public const string RateCardCreated = "billing.rate_card.created";
    public const string RateCardUpdated = "billing.rate_card.updated";
    public const string RateCardDeleted = "billing.rate_card.deleted";

    // Invoices
    public const string InvoiceGenerated = "billing.invoice.generated";
    public const string InvoiceIssued = "billing.invoice.issued";
    public const string InvoicePaid = "billing.invoice.paid";

    /// <summary>Emitted on <c>PayInvoice</c> when the caller-supplied
    /// <c>?tenantId</c> does not match the dunning record's tenant or the
    /// <c>invoiceId</c> shape is invalid. Severity: warning. Closes
    /// <c>PREPUB-2026-05-09-BILL-002</c>.</summary>
    public const string InvoicePayAttempted = "billing.invoice.pay_attempted";

    // Quotas + dunning
    public const string TenantQuotaUpdated = "billing.tenant_quota.updated";
    public const string DunningPaused = "billing.dunning.paused";

    /// <summary>Pre-existing constant for the <c>ResumeDunning</c> handler;
    /// declared here for symmetry. Existing emissions use the literal string
    /// today and continue to work — this constant is the going-forward shape.</summary>
    public const string DunningResumed = "billing.dunning.resumed";
}
