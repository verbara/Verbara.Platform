# DPIA-lite — Autonomous typification disposition

- **Date:** 2026-07-01
- **Author:** Harol (solo operator / DPO-by-default)
- **Feature:** autonomous AI disposition enrichment of the abandoned-wrap-up auto-close ([Platform/ADR-0034](../decisions/0034-autonomous-typification-disposition.md), OpenSpec change `typification-autonomous-disposition`)
- **Status:** Assessment complete — feature ships **dark (per-tenant flag OFF)**; this record is the self-attested Data Protection Impact assessment that replaces the intractable "obtain external legal sign-off" task (ADR-0034 Decision 7).

This is a **DPIA-lite**: a proportionate assessment for a low-risk processing change by a solo operator. It is not a full Art. 35 DPIA (none is triggered — see §5).

## 1. The processing

When a conversation wrap-up is **abandoned** (the agent never coded it) and the wrap-up-timeout worker auto-closes it, for tenants who have explicitly opted in the system may **stamp the pending high-confidence (≥ 0.95) AI suggestion as the conversation's disposition** (an internal outcome code, e.g. `Sales > Upgrade > Completed`), instead of closing it blank as today. The disposition feeds the tenant's internal analytics/reporting/routing.

- **Data subject:** the **end contact** (the customer who messaged the contact center via WhatsApp/SMS/web/etc.).
- **Controller:** the **tenant** (decides purposes and means of its contact-center processing).
- **Processor:** **Verbara** (provides the tooling under the tenant's documented instruction).
- **Personal data involved:** the disposition **node path** (interaction-category labels — not customer attributes), the AI **confidence**, timestamps, and the linkage to the conversation/contact. No message content, name, phone, or free text is written into the disposition or its audit record.

## 2. Lawful basis / role allocation

The tenant-admin **activation gate** is a **documented controller instruction** under the data-processing agreement (Art. 28(3)(a)) plus a configuration switch — **it is NOT the data subject's consent** and is never represented as such (Art. 22(2)(c) consent would be the *contact's* to give). Verbara-as-processor acts only on the controller's instruction; the controller warrants it has its own lawful basis for the underlying contact-center processing.

## 3. Art. 22 analysis (the load-bearing question)

**Conclusion: GDPR Art. 22 does not apply.** Art. 22(1) covers decisions "based **solely** on automated processing … which produce **legal effects** … or **similarly significantly** affect" the data subject. A typification is internal **interaction call-coding** for the controller's analytics/routing; it does not determine a right, deny a service, set a price, score the contact, or otherwise alter the contact's legal or material situation (the WP29/EDPB bar for "significant effect"). The contact typically never sees it. The effect is on the *controller's operations*, not the *subject's circumstances*. Because Art. 22 does not apply, the heavyweight consent/contest apparatus of the original framing is not required; the processing is instead governed by Art. 5 (accuracy, purpose limitation) and Art. 13–14 transparency.

**Residual-risk safeguards shipped anyway** (defence-in-depth, cheap, and good product hygiene):

- **Accuracy (Art. 5(1)(d)):** a confidence floor (≥ 0.95) + a commit-time verification pass; a supervisor **correction** path (append-only) that records the fix and feeds a dispute-rate signal to the existing calibration gate; per-tenant + global kill switches and a rate cap so a miscalibrated model cannot sweep a backlog unnoticed.
- **Transparency (Art. 13(2)(f)/14/15):** every autonomous decision is an **AI-actor audit record** (`actor_type = ai`) with the node path + confidence — the controller can surface "this was decided by AI" and the logic (a confidence-thresholded classifier) to a contact on request. Notice to the contact is the **controller's** duty; Verbara provides the record.
- **Storage limitation (Art. 5(1)(e)):** decision/audit records are tamper-evident but **time-bounded** — retained for the correction window then purged by the normal retention sweep (the "never delete" of the original framing was itself unlawful and was dropped).
- **Right to erasure (Art. 17):** on contact erasure the audit record's **contact linkage is redacted** while the decision-fact is retained under the Art. 17(3) legal-defence exemption.

## 4. Human involvement

The autonomous path is one position on an opt-in gradient that is **manual by default** (`Manual → SuggestOnly → AutoFill → Autonomous`). It applies only to **abandoned** wrap-ups no human coded, only when a supervisor-level admin has opted the tenant in, only above a high confidence floor, and any output is correctable by a human within the window. There is no data-subject-facing "dispute" portal (the contact is login-less and Art. 22 does not apply); correction is operator tooling.

## 5. Art. 35 DPIA trigger check

No Art. 35(3) trigger is met: this is **not** systematic evaluation/scoring producing legal/significant effects (§3), **not** large-scale special-category processing, **not** large-scale systematic monitoring of a public area. A full DPIA is therefore not required; this proportionate assessment suffices.

## 6. EU AI Act note

An AI system classifying contact-center interactions is plausibly **limited/minimal-risk** (not prohibited, not Annex III high-risk — no biometric categorisation, no credit/employment/essential-service scoring). The main obligation is transparency, satisfied by the AI-actor audit + provenance already shipped. Revisit if scope expands toward decisions that affect the contact.

## 7. Decision & kill-switch

**Ship dark.** `AutonomousDispositionEnabled` defaults **OFF**; enablement is per-tenant behind the activation gate + a global circuit breaker (config, no redeploy). Pilot on one or two tenants with a high confidence floor before wider rollout. Re-open this assessment if: the disposition taxonomy starts feeding an automated decision *about* the contact; special-category data enters the node path; or the confidence floor / correction window materially change.
