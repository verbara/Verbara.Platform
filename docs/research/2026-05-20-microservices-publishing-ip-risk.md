# Microservices publishing & Pro-IP risk analysis (Renderer / Realtime / Mail)

**Date:** 2026-05-20
**Context:** ADR-0022 Phase D shipped the API as Native AOT. The 3 non-AOT-able
microservices (Realtime, Renderer, Mail) are not yet published as images. This
analyzes the Pro-IP risk of publishing them and the public-vs-private decision.

---

## TL;DR

- **Publish all 3 as PUBLIC images, cosign-signed.** Do NOT make Realtime private.
- The Pro-IP risk of Realtime shipping as IL is **LOW** (no crown-jewel Pro, crypto
  licensing, only plumbing). Making it private would block trials/adoption for no
  real IP gain, and is inconsistent with the already-public AOT API image.
- Do **not** remove `Pro.Cluster` from Realtime (it's used + can't be removed from
  the closure anyway — see below). My initial suggestion was wrong on 3 counts.

## What actually ships inside the Realtime IL image (measured, not assumed)

The `.csproj` references 4 Pro packages; the **transitive closure ships 7 Pro DLLs**:

| DLL | What it is | IP value |
|-----|-----------|----------|
| `Pro.Push` | in-process pub/sub bus | low (plumbing) |
| `Pro.Push.SignalR` | presence **OR-Set CRDT** + Hub + event DTOs | low (textbook algorithm + DTOs) |
| `Pro.Cluster` (+`.Storage.Postgres`) | node coordination / leader election (leader-election not yet active) | low (standard patterns) |
| `Pro.MultiTenant` | tenant isolation | low-moderate (plumbing) |
| `Pro.Storage.Common` | SQL helpers | low (utility) |
| **`Pro.Licensing`** | ECDSA-P256 license **signature verification** | **low — see below** |

**NOT present:** `Pro.Dialer`, `Pro.Analytics`, `Pro.CallAnalytics`, `Pro.AgentAssist`,
`Pro.EventStore`. The crown-jewel Pro IP lives in the **AOT API** and never ships as IL.

### Dependency chain (why the "extra" DLLs appear)
```
Realtime → Pro.Push.SignalR → Pro.Cluster → Pro.Licensing
                            → Pro.MultiTenant
         → Pro.Cluster.Storage.Postgres → Pro.Cluster → Pro.Licensing
                                        → Pro.Storage.Common
```
Removing Realtime's *direct* `Pro.Cluster` reference does **not** drop it: `Pro.Push.SignalR`
(which Realtime genuinely needs) pulls `Pro.Cluster` → `Pro.Licensing` transitively.

## Why `Pro.Licensing` as decompilable IL is LOW risk (the lock & key)

`LicenseValidator` verifies licenses with **ECDSA P-256 signatures** using a **public key**
("Verify signature first — reject tampered keys"). The **private signing key never ships** —
it lives in Verbara's issuer (the website Worker).

- Decompiling reveals the *algorithm* (standard ECDSA) + the *public* key → **cannot forge
  licenses** without the private key. This is correct design (Kerckhoffs: security rests on
  the key, not code secrecy). Client-side validators are unhiddable in **any** language —
  even Native AOT can be patched.
- Tampering (patching the validator to always-pass) is defended by **cosign image signature +
  digest binding (Pro/ADR-0011)**, independent of IL-vs-AOT: a patched image fails signature
  verification and is rejected by the admission policy (Layer B).

## Public vs Private — and why Realtime should be PUBLIC

Measured facts:
- The **API image is already PUBLIC** on ghcr.io (anonymous pull works). Crown-jewel Pro IP
  is protected there by **AOT**, not by registry access control.
- The product **runs without a license** (validator never throws at startup; grace period +
  permissive paths) → **freemium/open-core**: anyone can run the stack; Pro features gate at
  runtime.

Therefore:
- **Private Realtime would block trials**: an evaluator running `docker compose up` couldn't
  pull a private image without a Verbara-issued token → the full stack fails to start. An
  adoption blocker for **zero** IP gain (the crown jewels aren't in Realtime).
- **Public + cosign-signed** is consistent with the API image and keeps the IP protection
  where it belongs: AOT (crown jewels) + runtime license gate + cosign (tamper).

## Recommendation

1. Publish **Renderer, Mail, Realtime** as **PUBLIC, cosign-signed** images (mirror the API
   release workflow), each on its own version tag.
2. Do **not** alter Realtime's Pro dependencies (low risk; refactor not justified).
3. Add an **ADR** codifying the nuanced rule + a build **guard**: non-AOT microservices
   (`Realtime/Renderer/Mail`) MUST NOT reference crown-jewel Pro packages
   (`Pro.Dialer/Analytics/CallAnalytics/AgentAssist/EventStore`). That is the load-bearing
   invariant to enforce — not the presence of Licensing/Cluster/plumbing.

## Corrections made during this analysis (honesty log)
- First said Realtime carries only "4 plumbing libs" → measured closure is **7**, incl.
  `Pro.Licensing` + `Pro.MultiTenant`.
- First suggested "remove `Pro.Cluster`" → it's **used** (cluster-node-state relay), **can't**
  be removed from the closure (Push.SignalR pulls it), and is **unnecessary** (low risk).
- First leaned "Realtime private" → should be **PUBLIC** (public AOT API + freemium model;
  private blocks trials for no IP gain).
