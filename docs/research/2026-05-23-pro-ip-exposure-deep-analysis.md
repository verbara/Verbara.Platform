# Should `api / realtime / renderer / mail` be public on ghcr.io? — deep IP-exposure analysis

> **Date:** 2026-05-23
> **Question:** the 4 Verbara platform service images on `ghcr.io/verbara/platform/{api,realtime,renderer,mail}` are currently public. Should they be? What's the actual IP exposure surface, what's the actual protection model, and does the existing decision hold up to scrutiny?
> **Verdict:** **YES, public is correct.** The decision was already made deliberately in [ADR-0023](../decisions/0023-publishing-non-aot-microservices.md) (Proposed 2026-05-20) and the implementation is consistent. This document validates that reasoning against the realities of the codebase (including Phase A.5 changes shipped today) and rejects two plausible counter-arguments.
> **Correction:** supersedes the wrong finding #1 in [`2026-05-23-image-source-audit.md`](2026-05-23-image-source-audit.md) which mis-measured visibility and claimed the 4 images were private. They were already public.

## 1. What's actually inside each image (IP exposure surface)

| Image | AOT? | Pro packages bundled (direct + transitive) | Crown-jewel Pro packages | Decompilable IP exposure |
|-------|------|-------------------------------------------|--------------------------|--------------------------|
| **api** | ✅ Native AOT (ELF, stripped, 67 MB) | **ALL** Pro engines + plumbing — Dialer, EventStore, Analytics, CallAnalytics, AgentAssist, MultiTenant, Routing, Cluster, Push, Licensing, Storage.Common, OpenTelemetry | ALL of them | **0 managed Verbara DLLs** in `/app/` (verified during C.4 today: `ls publish/Verbara*.dll | wc -l` returned 0). Algorithms are in native machine code; recovering them requires IDA Pro + manual reverse-engineering, NOT `ilspy`. |
| **realtime** | ❌ IL (managed DLLs, `aspnet:10.0` base) | **direct:** Push, Push.SignalR, Cluster, Cluster.Storage.Postgres. **transitive:** MultiTenant, Licensing, Storage.Common | **none** — guard `BanCrownJewelProInNonAotMicroservices` (Directory.Build.props) blocks any direct reference to Dialer/Analytics/CallAnalytics/AgentAssist/EventStore/Routing | Pro.Push/Push.SignalR/Cluster/MultiTenant/Licensing as IL. Decompilable with ilspy / dotPeek. |
| **renderer** | ❌ IL | **direct:** none. **transitive (via Verbara.Platform.Core):** MultiTenant | none | Pro.MultiTenant as IL. |
| **mail** | ❌ IL | **direct:** none. **transitive (via Verbara.Platform.Core):** MultiTenant | none | Pro.MultiTenant as IL. |

**Crown-jewel inventory** (per ADR-0023): `Pro.Dialer*`, `Pro.Analytics*`, `Pro.CallAnalytics*`, `Pro.AgentAssist*`, `Pro.EventStore*`, `Pro.Routing*`. These are the business-logic engines that differentiate Verbara from open-source CPaaS alternatives. **All six families ship ONLY inside the Native-AOT API image** — never as IL in any other shipped artifact.

**Non-crown-jewel Pro inventory** (acceptable as IL per ADR-0023): `Push*`, `Cluster*`, `MultiTenant`, `Licensing`, `Storage.Common`, `OpenTelemetry`. Plumbing / infrastructure / textbook-pattern implementations. Decompiling them yields well-documented patterns (Redis pub/sub bridge, distributed lock, tenant scoping, ECDSA validation), not competitive moats.

## 2. The protection model that's actually in place

The IP protection is **defense-in-depth**, not single-layer:

### Layer A — Native AOT for the crown jewels (ADR-0022)
The most valuable IP — outbound campaign scheduling (`Pro.Dialer`), live metrics aggregation (`Pro.Analytics`), post-call AI scoring (`Pro.CallAnalytics`), real-time agent suggestions (`Pro.AgentAssist`), event sourcing (`Pro.EventStore`), skill-based routing (`Pro.Routing`) — is compiled to native machine code by `dotnet publish -p:PublishAot=true` and ships in the API image only. Today's verification: `file src/Verbara.Platform.Api/bin/Release/net10.0/linux-x64/publish/Verbara.Platform.Api` returns `ELF 64-bit LSB pie executable, x86-64, ..., stripped`. There is no `Verbara.*.dll` managed assembly in the publish output to decompile.

Decompiling requires native-code reverse-engineering tools (IDA Pro, Ghidra, radare2) and human-hour-grade work to recover algorithmic intent. Far cry from "right-click → decompile to C#" on a managed DLL. This is the same protection level commercial native software has used for 30+ years (Adobe Photoshop, AutoCAD, Oracle DB).

### Layer B — License gate at runtime (Pro.Licensing.LicenseGuard + LicenseGateMiddleware)
[`LicenseGateMiddleware.cs:72`](../../src/Verbara.Platform.Api/Middleware/LicenseGateMiddleware.cs#L72) returns HTTP 402 `PaymentRequired` (RFC 9457 ProblemDetails with `trial_url` + `upgrade_url` extensions) on any Pro endpoint call when the running license is absent or expired. The product runs in OSS / community mode out of the box — pulling the public image and starting it without a `.lic` file works; only commercial features are gated. Free Tier 0.5 developer licenses are available at `https://verbara.io/developer-license`.

This is the FREEMIUM model. The image being public is not an IP leak — it's the trial download.

### Layer C — Image-digest binding (ADR-0011)
The license file embeds an `AuthorizedImageDigests` array. At startup, the Pro license guard reads the running container's manifest digest from the `IMAGE_DIGEST` env (set by the K8s chart / compose template). If the running digest is not in the license's authorized list, Pro endpoints stay gated even with a "valid" license file. This means an attacker who recovers the binary, patches it, and rebuilds an image gets a new digest that doesn't match any issued license. The license-issuance endpoint embeds only the digests tracked in [`verbara-website/data/authorized-digests.json`](/media/Data/Source/Verbara/verbara-website/data/authorized-digests.json) (today the file lists v2.4.0 → v2.4.2 + deprecated tail).

### Layer D — Cosign signatures
Every shipped image has a cosign Ed25519 signature anchored at the key pair `~/.verbara/keys/cosign.{key,pub}`. The customer-verifiable form is `cosign verify --key https://verbara.io/keys/cosign.pub ghcr.io/...`. Tampering breaks the signature; sat the `--insecure-ignore-tlog` flag is documented in customer manuales (the Verbara signing posture skips Rekor transparency log — see Phase A.5 closure commit `01c455f` for that posture's continuity).

### Layer E — Pro NuGet packages stay private
The Pro **source code** lives in a closed-source GitHub repo (per ADR-0018 Decision 3). The Pro **NuGet packages** are published to GitHub Packages with **private** visibility — a developer wanting to build against the Pro packages must authenticate. The IMAGES are public; the PACKAGES are not. So:
- A customer pulls an image and runs it → succeeds (image is public).
- A developer wants to compile against Pro APIs in their own code → blocked (no Pro NuGet access without commercial agreement).
- A reverse-engineer extracts Pro DLLs from the image → gets IL (non-crown-jewel) or native code (crown-jewel).

This separation is intentional. Distribution of compiled-form binaries (= images) is the customer experience; source-form distribution (= NuGet packages) is the developer experience and is license-gated.

## 3. Cross-check against industry precedent

Verbara is in the well-trodden **commercial open-core** space. Comparable products and their image-visibility postures:

| Product | Public image? | Crown-jewel protection |
|---------|---------------|------------------------|
| GitLab CE / EE | YES — `gitlab/gitlab-ee` is public | EE features need license file; binary is the same. |
| Mattermost Team / Enterprise | YES — `mattermost/mattermost-enterprise-edition` is public | E20 features need license file. |
| Sourcegraph | YES — `sourcegraph/server` is public | Tier-gated; native and IL both. |
| Grafana Enterprise | YES — `grafana/grafana-enterprise` is public | Enterprise features need license. |
| ELK / Elastic Stack | YES — `elastic/...` is public | X-Pack features license-gated. |
| SonarQube Community/Developer | YES — `sonarqube:community-edition` etc. all public | Tier-gated by license. |
| Oracle Database XE / Enterprise | (XE only public; EE not) | NOT comparable — Oracle EE is a 100% commercial product with no community tier. |

The dominant pattern in this segment is: **public image + license-gated features + source-form behind paywall**. Verbara matches this exactly. Going against it (private images) would be the outlier and would hurt funnel conversion, which ADR-0016 + ADR-0018 documented as the rationale for the Apache 2.0 license + go-public roadmap in the first place.

## 4. Phase A.5 re-check — do today's changes change anything?

Phase A.5 added new code to `Verbara.Sdk.Pro.Cluster` (the leader-election subsystem) and a brand-new SDK package `Verbara.Sdk.Cluster.Postgres` (MIT, on nuget.org). Per the classification in §1:

- `Verbara.Sdk.Cluster.Postgres`: MIT/SDK package. Not a Pro package. No IP concern — already public on nuget.org.
- `Verbara.Sdk.Pro.Cluster` (now ships with `IClusterLeader` + `LeaderElectionService` + `PostgresDistributedLock`-via-IDistributedLock + `ClusterLeadershipMetrics` + `VerbaraClusterOptionsBuilder`): **non-crown-jewel** per ADR-0023's classification ("Cluster*" is in the allowed-IL list). The newly-added leader election is **plumbing** (a renewal loop over an interface that the SDK defines) — implements a well-known pattern (Postgres TTL upsert; Hangfire and many others do similar). Not a competitive moat.
- `Verbara.Platform.Realtime` ships the above Pro.Cluster DLL as IL. The `BanCrownJewelProInNonAotMicroservices` build guard in [Directory.Build.props:67-80](../../Directory.Build.props#L67) successfully blocks any crown-jewel Pro from sneaking in. The guard activates ONLY for `Realtime`, `Renderer`, `Mail` projects (per the `Condition=` clause). v2.4.2 today builds clean against this guard.

**Conclusion:** Phase A.5 does NOT change the IP-exposure picture. The new code lands in the already-acceptable IL surface; the crown-jewel boundary is intact and machine-enforced.

## 5. Counter-arguments considered and rejected

### CA1 — "Private images would protect more IP, why not just?"
The IL surface exposed by public Realtime/Renderer/Mail is non-crown-jewel plumbing (presence CRDT, distributed lock loop, tenant scope filter, ECDSA verifier). These are textbook patterns; competitors building their own contact center will write functionally identical code. **There is no actual IP being protected by making these images private.** The cost (adoption friction, contradicts ADR-0018 open-core narrative, contradicts ADR-0023's explicit decision) is non-trivial and the benefit is zero.

### CA2 — "What about `Pro.Licensing` shipping as IL? Couldn't someone decompile it and forge licenses?"
`Pro.Licensing` validates licenses against an **ECDSA-P256 public key embedded in the binary**. The corresponding **private signing key never ships anywhere** — it lives in the Verbara license-issuance infrastructure only (verbara.io / cloudflare-workers). Decompiling the validator reveals the public key + the validation algorithm. Both are **already public information by Kerckhoffs's principle** — security comes from the private signing key, not from algorithm obscurity. An attacker who decompiles `Pro.Licensing` gains: knowledge that ECDSA-P256 is used (already documented in the customer-facing manuales). Forging a license requires recovering the private signing key, which is not in any shipped artifact. **The license enforcement layer is cryptographically safe to ship as IL.**

Layer C (image-digest binding via ADR-0011) compounds the protection: even if an attacker successfully patched `Pro.Licensing` to skip validation, the patched image has a different digest, and the next license they'd try to use embeds only authorized-digest entries — so the patched build runs in OSS mode anyway. The license-gate middleware doesn't trust `Pro.Licensing` alone; it trusts `Pro.Licensing.LicenseGuard.ValidateAsync()` AND the runtime-digest-vs-license-claim check.

### CA3 — "Tampering risk for self-hosted customers — they could swap in a patched image"
A customer pulling the public `api` image and replacing the binary with a tampered version gets a new manifest digest. Their issued license's `AuthorizedImageDigests` claim doesn't list the tampered digest → Pro endpoints stay 402-gated. The customer would have nothing — no Pro features, no operational benefit, just an unsupported deviation from the shipped binary. They could enable OSS-mode by removing the license file entirely, but that's already available via the public image's documented "no .lic = OSS mode" path. No new attack vector created by public images.

### CA4 — "Customer K8s deploys with private images have one extra step (image-pull secret) — but that's trivial, why not keep private?"
Trivial per deploy, but the cumulative tax is real:
- Every prospective customer wants to evaluate before committing → friction → some bounce.
- Every SMB manual must document PAT setup.
- Every CI/CD example, every IaC template, every K8s tutorial we ship needs the secret-wiring step.
- The same workflow that's `docker compose up` for the open-source comparison products becomes `gh auth login; gh auth token | docker login ghcr.io ... ; docker compose up` for Verbara.
- ADR-0018 explicitly rejected the "private forever" path on funnel-economics grounds (ADR-0016 funnel modeling: 1000 visitors/month → 3 conversions at $30k = $1.08M ARR; even a 30% bounce drop drops ARR by ~$300k/yr).

## 6. The actual verdict + actions

**Verdict: stay public.** The decision was correct when [ADR-0023](../decisions/0023-publishing-non-aot-microservices.md) was Proposed (2026-05-20), the implementation is consistent (verified today: all 5 images publicly pullable via `docker pull` anonymous; build guard active; license layers operational; Phase A.5 additions land in the allowed-IL surface), and the counter-arguments don't hold.

**Actions taken in this session (already committed):**
- Audit document [`2026-05-23-image-source-audit.md`](2026-05-23-image-source-audit.md) finding #1 corrected with a strikethrough + link to this analysis (commit appending to `23317c4e`).
- This analysis document committed alongside (`docs/research/2026-05-23-pro-ip-exposure-deep-analysis.md`).

**Pending follow-up (maintainer-side):**
1. **Promote ADR-0023 from "Proposed" to "Accepted".** The implementation (4 images public, build guard active, license layers operational) is complete and verified today. Update the ADR's status header.
2. **Optionally: add a `docs/security/threat-model.md`** capturing this IP-exposure analysis formally. ADR-0018 Trigger #4 specifies a threat model as a precondition for the future repo-public flip; this analysis can seed Section "What public images expose vs what stays protected" of that document.
3. **Optionally: a self-test in CI** that runs `docker pull --quiet --platform linux/amd64 ghcr.io/verbara/platform/<svc>:<latest-tag>` without authentication to detect a regression if someone accidentally toggles visibility back to private. A `gh api ...` call also works (`GET /orgs/verbara/packages/container/platform%2Fapi` returns `"visibility":"public"|"private"`).
4. **Skip:** anything resembling "let's lock down image visibility" — the analysis above shows that's a net-negative move.

## 7. Open questions worth confirming

1. **Pro.Licensing source-license clause for compiled-form distribution.** The Pro license agreement / EULA should explicitly state that customers receive the right to RUN the compiled image but NOT to redistribute it or to use it without a paid license for commercial-feature-gated functionality. (The IMAGE is public; the COMMERCIAL USE of Pro features is still gated by license file.) Confirm this is documented in the Pro EULA — if not, [Pro plan `2026-05-08-pro-licensing-eula-overhaul.md` mentioned in ADR-0018 §5] is the place to land it.
2. **Threat model document.** ADR-0018 Trigger #4 demands one before the repo-public flip; this analysis is a strong precursor. Worth promoting to `docs/security/threat-model.md`.
3. **CI self-test for visibility regression.** Worth ~30 min of automation: a workflow step that fails if any platform image flips from public to private. Cheap insurance.
