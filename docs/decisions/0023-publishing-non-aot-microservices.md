# ADR-0023 — Publishing the non-AOT microservices (Realtime / Renderer / Mail)

**Status:** Proposed
**Date:** 2026-05-20
**Supersedes / extends:** [ADR-0022 — Platform.Api Native AOT shipping path](0022-platform-api-aot-shipping-path.md)
**Evidence:** [Microservices publishing & Pro-IP risk analysis](../research/2026-05-20-microservices-publishing-ip-risk.md)

## Context

ADR-0022 made `Verbara.Platform.Api` ship as **Native AOT** so closed-source Pro IP
never ships as decompilable IL. To unblock that, three pieces that **cannot** be AOT
(reflection-bound dependencies) were extracted into separate microservices:

- **Realtime** — SignalR Hub + presence (Pro.Push.SignalR) + Redis backplane.
- **Renderer** — PDF/CSV (QuestPDF + ScottPlot).
- **Mail** — SMTP/Graph (MailKit + Microsoft.Graph).

Only the **API image** is published today (`ghcr.io/verbara/platform/api`, public, AOT,
cosign-signed). The three microservices are not published, so a full deployment cannot
pull them — a production-readiness and adoption gap.

Key measured facts (see evidence doc):
- **Renderer + Mail reference ZERO Pro packages.** No IP concern.
- **Realtime's transitive closure ships 7 Pro DLLs** (`Push`, `Push.SignalR`, `Cluster`,
  `Cluster.Storage.Postgres`, `MultiTenant`, `Licensing`, `Storage.Common`) — but **none
  of the crown-jewel Pro packages** (`Dialer`, `Analytics`, `CallAnalytics`, `AgentAssist`,
  `EventStore`). Those live only in the AOT API.
- `Pro.Licensing` validates licenses with **ECDSA-P256 signatures** (public key embedded,
  private signing key never ships). Decompiling it cannot forge licenses (Kerckhoffs).
  Tampering is defended by cosign + digest binding (ADR-0011), independent of IL-vs-AOT.
- The **API image is already PUBLIC** and the product **runs without a license** (freemium;
  Pro features gate at runtime). IP protection lives in **AOT (crown jewels) + runtime
  license gate + cosign**, not in registry access control.

## Decision

1. **Publish Realtime, Renderer, and Mail as PUBLIC, cosign-signed images** on
   `ghcr.io/verbara/platform/{realtime,renderer,mail}`, mirroring the API release workflow.
   Realtime is **not** made private: that would block trials/POCs (`docker compose up` could
   not pull it without a Verbara-issued token) for **zero** IP gain, and is inconsistent with
   the already-public AOT API.

2. **Nuanced AOT shipping rule (refines ADR-0022's "every shippable image MUST be AOT"):**
   > Every shippable Verbara image MUST be Native AOT, **EXCEPT** microservices that cannot
   > be AOT due to reflection-bound dependencies (SignalR server dispatch, QuestPDF/ScottPlot,
   > MailKit/Graph). Those ship as IL **and MUST NOT reference any crown-jewel Pro package**,
   > so the only Pro IP that ever ships as decompilable IL is non-crown-jewel plumbing.

3. **Crown-jewel Pro packages** (forbidden in the non-AOT microservices):
   `Verbara.Sdk.Pro.Dialer*`, `Verbara.Sdk.Pro.Analytics*`, `Verbara.Sdk.Pro.CallAnalytics*`,
   `Verbara.Sdk.Pro.AgentAssist*`, `Verbara.Sdk.Pro.EventStore*`, `Verbara.Sdk.Pro.Routing*`.
   Non-crown-jewel Pro (`Push*`, `Cluster*`, `MultiTenant`, `Licensing`, `Storage.Common`,
   `OpenTelemetry`) MAY ship as IL in these microservices.

4. **Enforce with a build guard** (`BanCrownJewelProInNonAotMicroservices`) in the three
   microservice projects, modeled on the existing `BanDapperPackageReferences` guard: the
   build fails if a crown-jewel Pro package is referenced (directly) by Realtime/Renderer/Mail.

5. **Do NOT restructure Realtime's Pro dependencies** to drop `Pro.Licensing`/`Pro.Cluster`.
   The risk is low and the refactor (decoupling Pro.Push.SignalR → Pro.Cluster → Pro.Licensing
   in the Pro repo) is not justified.

## Consequences

- **Positive:** frictionless trials (`docker compose up` pulls everything anonymously, runs
  freemium); a reproducible production deploy (all images pullable); the load-bearing IP
  invariant (no crown-jewel IL) is machine-enforced, not just documented.
- **Negative / accepted:** non-crown-jewel Pro plumbing (presence CRDT, clustering, license
  validator) ships as decompilable IL in the public Realtime image. Accepted because it is a
  textbook-algorithm / crypto-safe surface, not a competitive moat.
- **Future:** if Realtime's leader-election (Phase A.5) or other features later pull a
  crown-jewel package, the guard will fail the build — forcing a conscious decision (keep that
  logic AOT-side) rather than a silent IP leak.
