# Tasks — reconcile-ai-credit-specs

## 1. Author artifacts

- [x] 1.1 proposal.md with tier/ownership/decision_ref frontmatter
- [x] 1.2 Delta specs for ai-credit-ledger (2 ADDED, 2 MODIFIED, 1 REMOVED)
- [x] 1.3 Delta specs for typification-platform-llm, ai-credit-metering, ai-credit-billing (1 MODIFIED each)
- [x] 1.4 design.md (ownership-over-merge rationale + code-grounding table)

## 2. Apply to living specs

- [ ] 2.1 Apply the requirement deltas to `openspec/specs/**` (archive flow applies them; verify the
      MODIFIED header matches took effect and the REMOVED requirement is gone)
- [x] 2.2 Fill the six `Purpose: TBD` placeholders (ledger, metering, billing, readout,
      test-determinism, typification-autonomous-disposition)
- [x] 2.3 typification-platform-llm frontmatter: `roadmap_ref` → `decision_ref: Platform/ADR-0032`
- [x] 2.4 typification-platform-llm: replace the change-shaped Architectural Risk tail with a
      capability-scoped note

## 3. Verification

- [ ] 3.1 `openspec validate --specs --strict` green (all 7 specs)
- [ ] 3.2 Spec-vs-code check of every rewritten requirement: `PostMeteredDebitAsync` signature
      (ICreditLedgerStore.cs), mint-worker registration (Program.cs), partner_admin has no
      `billing:credits:grant` (RoleTemplateSeeder.cs), QuotaOutcome mapping unchanged
- [ ] 3.3 No remaining intra-spec contradiction: grep the living specs for "inert", "coveredSource",
      "lands with the partner-scoped endpoint" — all gone
- [ ] 3.4 docs(openspec) PR green (docs-only; CI gates pass) and merged via merge queue

## 4. Closing

- [ ] 4.1 Archive the change (`openspec archive reconcile-ai-credit-specs`) — checkboxes first,
      lands via its own docs(openspec): PR per the standing archive-on-merge instruction
