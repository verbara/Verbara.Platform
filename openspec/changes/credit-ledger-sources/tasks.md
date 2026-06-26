> Tasks authored in full at the start of this change (after `credit-ledger-cutover` merges), re-grounded.
> Outline per ADR-0033:

## 1. Grant sources + allocation
- [ ] 1.1 TopUp/Promo/Partner grant posting (idempotent top-up on external_ref; promo expires_at).
- [ ] 1.2 Debit → source-lot FIFO allocation rows; partner draws to the partner-revenue ledger.
- [ ] 1.3 Invoice customer-owed = PostPaid-lot allocations; multi-source no double-count/cross-attribution.

## 2. API + RBAC
- [ ] 2.1 `ICreditLedgerService` (TopUpAsync, GetBalanceAsync, GetEntriesAsync paginated).
- [ ] 2.2 Endpoint group: top-up (operator/partner) + balance + entries; DTOs in `ApiJsonContext`.
- [ ] 2.3 Add `billing:credits:grant` + `billing:credits:read` to PermissionSeeder + RoleTemplateSeeder; RbacReseed note.

## 3. Verification
- [ ] 3.1 Build 0 warnings; full test green; AOT gate; CI green. Web widget = separate Platform.Web PR.
