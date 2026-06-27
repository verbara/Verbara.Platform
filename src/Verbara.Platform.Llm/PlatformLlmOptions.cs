namespace Verbara.Platform.Llm;

/// <summary>
/// Host-bound configuration for Verbara's <b>operator-managed</b> Typification
/// LLM (the provider served when a tenant sets <see cref="AiSource.PlatformManaged"/>).
/// The key lives only here — never per-tenant, never serialized to any DTO.
/// </summary>
public sealed class PlatformLlmOptions
{
    /// <summary>Operator master switch. When false, platform-managed tenants degrade to the empty suggestion.</summary>
    public bool Enabled { get; set; }

    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 800;
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Tokens per AI Credit (commercial unit). Default 1000. Credits = Σtokens ÷ this ratio (aggregate, never per-call).
    /// Also the <b>flat fallback</b> ratio for records whose metadata lacks the per-direction split.</summary>
    public long CreditTokenRatio { get; set; } = 1000;

    /// <summary>Tokens per AI Credit for <b>input</b> (prompt) tokens. Optional. Per-direction pricing is ACTIVE only
    /// when BOTH this and <see cref="OutputCreditTokenRatio"/> are non-null and &gt; 0; otherwise the flat
    /// <see cref="CreditTokenRatio"/> applies.</summary>
    public long? InputCreditTokenRatio { get; set; }

    /// <summary>Tokens per AI Credit for <b>output</b> (completion) tokens. Optional. Per-direction pricing is ACTIVE only
    /// when BOTH this and <see cref="InputCreditTokenRatio"/> are non-null and &gt; 0; otherwise the flat
    /// <see cref="CreditTokenRatio"/> applies.</summary>
    public long? OutputCreditTokenRatio { get; set; }

    /// <summary>
    /// Cutover kill-switch (default <c>false</c>) for the AI-credit ledger (ADR-0033, change b). When on, the
    /// AiAnalysis quota check and the metering funnel read the ledger projection + post a metered debit instead
    /// of recomputing from <c>usage_records</c> against the <c>AiCreditsMonthly</c> scalar; when off the legacy
    /// path runs unchanged. This gates <b>both</b> the quota and meter seams. The cutover flip order is
    /// enforcement → invoice-read-shadow → invoice-read, so this flag flips first (after back-fill + one
    /// mint-worker tick); see <see cref="LedgerInvoiceReadEnabled"/>.
    /// </summary>
    public bool LedgerEnforcementEnabled { get; set; }

    /// <summary>
    /// Cutover kill-switch (default <c>false</c>) for the invoice Σ-PostPaid flip (ADR-0033, change b). When on,
    /// <c>BuildAiCreditLineItemAsync</c> derives customer-owed AiAnalysis overage as the sum of the period's
    /// <c>PostPaid</c> debit rows instead of recomputing from <c>usage_records</c>; when off the legacy invoice
    /// path runs unchanged. This flag flips <b>last</b> — only after <see cref="LedgerEnforcementEnabled"/> is on
    /// and the shadow reconciliation confirms <c>Σ PostPaid == max(0, consumed − allowance)</c> per tenant.
    /// </summary>
    public bool LedgerInvoiceReadEnabled { get; set; }

    /// <summary>
    /// One-time cutover seed switch (default <c>false</c>) for the AI-credit ledger back-fill (ADR-0033, change b,
    /// task B7). The ratio basis that converts <c>usage_records</c> tokens to credits lives only in this
    /// <see cref="PlatformLlmOptions"/> config (not in raw SQL), so the current-period back-fill is a config-gated
    /// hosted service rather than a migration. The operator flips this on for the single cutover deploy: the
    /// <c>CreditLedgerBackfillService</c> seeds each AI-credit tenant's current-period Subscription grant and an
    /// idempotent covered-consumption debit (covered drawn from the grant, the remainder as a PostPaid tail) once,
    /// logs completion, then the operator flips it back off. The seed is idempotent (a <c>backfill:{periodKey}</c>
    /// marker row), so a re-run with the flag left on is a safe no-op; it overlaps harmlessly with the
    /// <c>CreditGrantMintWorker</c> (both grant idempotently on the same period key).
    /// </summary>
    public bool RunLedgerBackfill { get; set; }
}
