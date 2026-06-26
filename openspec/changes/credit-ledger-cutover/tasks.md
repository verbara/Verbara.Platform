> Tasks are authored in full at the start of this change (after `credit-ledger-substrate` merges),
> re-grounded against the then-current code. Outline per ADR-0033:

## 1. Quota cutover
- [ ] 1.1 Add `QuotaOutcome` to `QuotaCheckResult`; set it in both AiAnalysis branches; map `Allowed` from it.
- [ ] 1.2 `CheckQuotaAsync` (AiAnalysis) reads the projection balance; balance ≤ projected debit ⇒ `HardBlock`.
- [ ] 1.3 `ConversationEndpoints` switches on `result.Outcome`; drop the `GetQuotaStatusAsync` re-read.

## 2. Metering cutover
- [ ] 2.1 `BillingTypificationCreditMeter.RecordAsync` posts the atomic guarded debit (post-LLM, best-effort).

## 3. Invoicing cutover
- [ ] 3.1 `BuildAiCreditLineItemAsync` derives post-paid remainder from ledger debits vs the subscription grant.

## 4. Subscription grant + back-fill
- [ ] 4.1 Scheduled mint worker (idempotent `(tenant, period_key)`), mirrors `OverageInvoiceIssuanceWorker`.
- [ ] 4.2 One-time current-period back-fill migration; feature flag gates the invoice-read flip until complete.

## 5. Verification
- [ ] 5.1 Change-(a) characterization tests stay green (allowance-only behaviour byte-identical).
- [ ] 5.2 Build 0 warnings; full test green; AOT gate clean; CI green.
