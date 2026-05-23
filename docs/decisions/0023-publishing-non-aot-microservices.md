# ADR-0023 — Publishing the non-AOT microservices (Realtime / Renderer / Mail)

**Status:** Accepted
**Date:** 2026-05-20 (Proposed) · 2026-05-23 (Accepted — implementation complete + empirically validated)
**Supersedes / extends:** [ADR-0022 — Platform.Api Native AOT shipping path](0022-platform-api-aot-shipping-path.md)
**Evidence:**
- Initial proposal: [Microservices publishing & Pro-IP risk analysis](../research/2026-05-20-microservices-publishing-ip-risk.md) (2026-05-20)
- Acceptance validation: [Deep IP-exposure analysis](../research/2026-05-23-pro-ip-exposure-deep-analysis.md) (2026-05-23) — re-grounds the reasoning against the live codebase (incl. Phase A.5 additions) and rejects two plausible counter-arguments

## Status history

- **2026-05-20 — Proposed.** Phase 5 cutover (v2.4.1 shipped 4 cosigned images: api/realtime/renderer/mail). Decision authored alongside the release.
- **2026-05-23 — Accepted.** Implementation verified end-to-end:
  - All 5 packages (`api`, `realtime`, `renderer`, `mail`, `web`) confirmed anonymously pullable via `docker pull ghcr.io/verbara/platform/<svc>:v2.4.1` after `docker logout ghcr.io`.
  - `BanCrownJewelProInNonAotMicroservices` build guard present + active in [Directory.Build.props:67-80](../../Directory.Build.props#L67) — fires only for `Verbara.Platform.{Realtime,Renderer,Mail}` projects, blocks Dialer/Analytics/CallAnalytics/AgentAssist/EventStore/Routing PackageReferences.
  - API Native AOT publish output verified: 0 managed Verbara DLLs in `publish/`, native ELF 67 MB stripped (matches `file Verbara.Platform.Api` → "ELF 64-bit LSB pie executable, x86-64, ..., stripped").
  - Phase A.5 (shipped same day in `v2.4.2` train) added new code to `Verbara.Sdk.Pro.Cluster` (`IClusterLeader` + `LeaderElectionService` + `PostgresDistributedLock`-via-IDistributedLock) — all non-crown-jewel per this ADR's classification; guard remains green; no IP-exposure regression.
  - Counter-arguments validated and rejected (private-for-IP-protection: zero IP gain, real friction cost; ECDSA-public-key-as-IL: cryptographically safe by Kerckhoffs; tampered binary: defeated by ADR-0011 Layer C digest binding; CI/CD friction tax: real, would harm ADR-0016 funnel economics).

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

## Post-acceptance verification (2026-05-23)

Phase A.5 (Realtime per-resource leader election) shipped today. The new package references in
`Verbara.Platform.Realtime.csproj` are `Verbara.Sdk.Cluster.Primitives` (SDK MIT, public on
nuget.org) and `Verbara.Sdk.Cluster.Postgres` (SDK MIT, new in 2.2.1, public on nuget.org), plus
the existing `Verbara.Sdk.Pro.Cluster` reference at 2.5.1-pro which now carries the new
`IClusterLeader` API. The build guard's classification holds:

| New surface | Where it ships | Classification | Verdict |
|-------------|----------------|----------------|---------|
| `Verbara.Sdk.Cluster.Postgres.PostgresDistributedLock` | SDK MIT package on nuget.org | open-source primitive | ✅ public by license |
| `Verbara.Sdk.Pro.Cluster.Leadership.IClusterLeader` + `LeaderElectionService` | Realtime image (as IL); also reachable through Pro.Cluster everywhere | non-crown-jewel Pro plumbing | ✅ acceptable as IL per this ADR |
| `cluster_distributed_lock` Postgres table | runtime artifact, not in any image | n/a | ✅ |

The leader-election implementation is a textbook TTL-upsert renewal loop on top of the existing
`IDistributedLock` primitive. Decompiling it reveals a well-known pattern (Hangfire, Akka.NET
cluster singleton, K8s leases all use functionally identical algorithms). Not a competitive
moat. The empirical IP-exposure picture is unchanged.

## Pending follow-up (optional, not blocking acceptance)

1. **Threat model document.** [ADR-0018 Trigger #4](0018-visibility-decision-3-private-now-public-on-trigger.md#trigger-checklist-must-all-be--before-flipping) requires a `docs/security/threat-model.md` before the repo-public flip. The deep IP-exposure analysis [`2026-05-23-pro-ip-exposure-deep-analysis.md`](../research/2026-05-23-pro-ip-exposure-deep-analysis.md) is a strong precursor for the "What public images expose vs what stays protected" section.
2. **CI visibility-regression self-test.** A 30-minute workflow step that calls `GET /orgs/verbara/packages/container/platform%2F<svc>` via `gh api` and asserts `.visibility == "public"`. Detects accidental private-flips on any of the 5 packages. Cheap insurance.
3. **Pro EULA wording alignment.** Confirm the Pro license agreement explicitly grants right-to-run for the public compiled image while gating commercial-feature use to a paid license. Tracked in [`Verbara.Sdk.Pro/docs/plans/active/2026-05-08-pro-licensing-eula-overhaul.md`](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/plans/active/2026-05-08-pro-licensing-eula-overhaul.md) (private repo).
