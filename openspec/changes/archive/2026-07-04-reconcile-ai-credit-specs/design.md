# Design — reconcile-ai-credit-specs

## Approach

Delta-based, forward-only reconciliation. The 12 archived changes are append-only history and are
NOT touched; only the living specs move. The capability boundary is **kept at four specs** — the
split (metering = pricing basis, ledger = accounting/enforcement, billing = invoicing/dunning,
readout = UI) is defensible; what was broken is *ownership*: the same outcome (quota decision,
overage amount) was specified unconditionally in multiple specs. The fix is an explicit ownership
requirement in `ai-credit-ledger` plus flag-conditioning language in the two legacy accounts, rather
than a risky spec merge/rename that would break references and history.

Authoritative sources for every rewritten claim (verified against `main` before authoring):

- `PostMeteredDebitAsync(TenantId, decimal, string?, CancellationToken)` —
  `src/Verbara.Platform.Billing/ICreditLedgerStore.cs:72` (no `coveredSource` parameter; lot
  allocation owns source-tagging).
- `CreditGrantMintWorker` registered unconditionally — `Program.cs` (`AddHostedService`), writes
  Subscription grants at runtime → the inert-substrate requirement is falsified.
- c2 RBAC resolution — Platform/ADR-0033 (c2)-resolution addendum: operator-minted Promo/Partner
  grants, no new RBAC in c2; `partner_admin` does not hold `billing:credits:grant` (RoleTemplateSeeder).
- Warn-overflow, shadow-gated invoice flip, ratio-freeze — Platform/ADR-0033 addenda.
- Entitlement re-check placement — Platform/ADR-0032.

## Non-requirement living-spec edits (applied directly, not via deltas)

Delta files carry only requirement operations; these hygiene edits are applied directly to the
living specs in the same PR:

1. Fill `## Purpose` (currently `TBD`) in: `ai-credit-ledger`, `ai-credit-metering`,
   `ai-credit-billing`, `ai-credits-readout`, `test-determinism`,
   `typification-autonomous-disposition` — one-paragraph capability statements.
2. `typification-platform-llm` frontmatter: `roadmap_ref: …#typification-p2c` (dangling) →
   `decision_ref: Platform/ADR-0032`.
3. `typification-platform-llm`: replace the change-shaped `## Architectural Risk` tail (describes
   only the original P2c.2 change) with a short capability-scoped risk note.

## Rejected alternatives

- **Merging the four specs into one** — loses stable names, bloats a single file past reviewability,
  and rewrites history for a problem that is ownership-clarity, not file count.
- **Deleting the legacy accounts outright** — the legacy paths still run in production (flags are
  default-off); their behaviour must stay specified until the flags flip permanently.
