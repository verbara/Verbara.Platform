# ADR-0009: SLO baseline + alert severity model

**Status:** Accepted
**Date:** 2026-04-26
**Context:** R5.4 Production Validation Track A

## Context

R5.4 ships the first canonical SLOs + Prometheus alert rules. Two design
decisions need locking before S5.2/S5.3 execute:

1. How are SLO numeric targets chosen — aspirational or data-derived?
2. How are alerts classified for operator triage?

Premature aspirational SLOs cause alert fatigue + false confidence ("SLO met"
when actually under-load). 3-tier alert classification is industry standard
(Google SRE book) and matches the Prometheus convention.

## Decision

**SLOs are derived from S5.1 load test results, not aspirational.**

- S5.2 (`docs/operations/slos.md`) is written **after** S5.1 first run completes.
- Each SLO references the meter/counter that measures it (one of the 17 Pro
  meters or Platform meters).
- Targets use observed p50/p95/p99 from S5.1 with a 20% headroom margin.
- "v1 baseline" SLOs are explicitly conservative; "v2 enterprise" SLOs are
  documented as aspirational targets with hardware/scale assumptions.

**Alert severity model is 3-tier:**

- **P0 (page on-call):** API availability < 99% over 5min · JWT validation
  latency p99 > 500ms over 5min · License guard blocked > 1% of requests over
  10min · Retention service stalled > 1h
- **P1 (ticket within 24h):** SLO breach without P0 trigger · Circuit breaker
  open > 5min · Presence backlog growing for 15min · Audit write latency p99 >
  1s sustained
- **P2 (review weekly):** Capacity warnings (DB connections > 80% pool) · Slow
  queries (>500ms) · Retention dryrun divergence from purge counter

Each alert has a 1-paragraph runbook entry in `docs/operations/alerts-runbook.md`
with **what / why / first response**.

## Alternatives considered

- **Aspirational SLOs first, refine later** — rejected: causes alert fatigue;
  documented as anti-pattern in Google SRE Workbook.
- **2-tier severity (critical / warning)** — rejected: collapses P1/P2 distinction,
  forces on-call to triage everything as urgent.
- **5-tier severity (P0-P4)** — rejected: more granularity than triage process
  warrants at current scale; can be expanded if needed.

## Consequences

- **Positive:** SLOs reflect actual capability; alerts have meaningful severity
  for operator workflow.
- **Negative:** SLO publication is gated by S5.1 completion (sequencing
  dependency in Phase A).
- **Neutral:** Alert thresholds are tunable post-ship via PR to `alerts.yml`
  with operator approval.

## References

- R5.4 spec § "Track A" S5.2 + S5.3
- Google SRE Workbook chapter "Alerting on SLOs"
- Existing meter catalog: `Asterisk.Sdk.Pro/docs/architecture.md` § "Meter catalog"
