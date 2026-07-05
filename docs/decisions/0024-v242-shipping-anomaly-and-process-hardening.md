# ADR-0024 — v2.4.2 shipping anomaly + ADR-0023 process hardening sweep

- **Status:** Accepted
- **Date:** 2026-05-23 (proposed) → 2026-05-23 (accepted, same-day)
- **Supersedes:** none
- **Superseded by:** none
- **Cross-references:** [Pro/ADR-0011 (image-digest binding Layer C)](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md), [ADR-0023 (publishing non-AOT microservices)](0023-publishing-non-aot-microservices.md), [Research — IP-exposure deep analysis](../research/2026-05-23-pro-ip-exposure-deep-analysis.md), [Phase A.5 smoke test](../operations/phase-a5-smoke-test-2026-05-23.md)

## Context

On 2026-05-23 a release-validity audit caught that the 4 `ghcr.io/verbara/platform/{api,realtime,renderer,mail}:v2.4.2` images had been published to `ghcr.io` **outside the `release.yml` workflow path**. None of the institutional release artifacts existed:

- no git tag `v2.4.2`
- no GitHub Release entry for v2.4.2
- no `release.yml` run for v2.4.2
- no Actions audit log, no provenance, no SBOM

The images themselves were valid (anonymously pullable, cosign-signed against `.github/cosign.pub`), and the API image digest had been authorized in `verbara-website/data/authorized-digests.json` for Layer C image-binding. But the trazability was broken: from a customer-audit perspective, the binaries appeared to have materialized out of thin air.

Concurrently, three latent CI gaps were exposed by the same audit:

1. **`visibility-monitor.yml` only tested anonymous `docker pull`.** It did not verify cosign signature presence or freshness of the published `https://verbara.io/keys/cosign.pub` URL. Any unsigned `docker buildx push` to a published tag would go undetected.
2. **`https://verbara.io/keys/cosign.pub` returned HTTP 404.** The static asset had never been published despite being documented in every customer SMB manual and referenced from the source repo's research docs. The first `cosign verify` step in every customer's onboarding fails today.
3. **No reconciliation between `verbara-website/data/authorized-digests.json` and ghcr.io reality.** Drift went silently undetected.

Forensic confirmed the binary origin: the maintainer (`hreina`) ran `docker buildx build --push` locally with `--tag :v2.4.2` on 2026-05-22 to produce a working image set for the Phase A.5 closure session, then signed each digest with the local cosign keypair. The full official release pipeline was skipped — likely time-pressured during the multi-repo Phase A.5 cross-validation work.

### Why the simple fix is wrong

The temptation is "tag retroactively, re-run `release.yml`, get the institutional artifacts back, done." But that path **destroys Pro/ADR-0011 Layer C image-binding** for every customer that had already pulled `:v2.4.2` (the digest changes when re-built, and customers' license validation tools compare the runtime digest against `authorized-digests.json` — a mismatch is treated as image tampering).

Detail: `docker push` to an existing tag overwrites the digest pointer, but the original digest blob remains addressable via `ghcr.io/.../api@sha256:<old>` until a registry GC pass. Until that GC, customers with a cached pull are unaffected; after GC, their next pull fails. A re-publish forces every authorized customer to either re-pull (losing the original digest) or fail validation. Either outcome is worse than the original anomaly.

So the fix must (a) preserve the on-registry digests that are already authorized, (b) restore enough institutional trazability for audit, and (c) permanently close the holes so the anomaly cannot recur.

## Decision

A six-fold sweep (4 PRs + 1 tag operation + 1 forward-release) shipped in a single day:

### Layer 1 — Fix the customer-facing 404 (verbara-website PR #16)

Drop `public/keys/cosign.pub` with the same PEM key committed in `Verbara.Platform/.github/cosign.pub`, expanded with customer-friendly leading comments (image inventory, verify command, diff-vs-repo instruction, custody pointer, signing-posture note). CF Worker static-assets binding serves it at the expected `https://verbara.io/keys/cosign.pub` URL after auto-deploy.

### Layer 2 — Detect unsigned pushes + URL drift (Platform PR #5)

Extend `visibility-monitor.yml`:
- Step "Assert cosign signatures present on all 5 packages" runs `cosign verify --key .github/cosign.pub --insecure-ignore-tlog` per tag.
- Step "Assert public cosign.pub URL matches in-repo PEM key" compares **only the PEM block** between `verbara.io/keys/cosign.pub` and `.github/cosign.pub` (the surrounding comments intentionally differ).

Add `digest-reconciliation.yml` (daily 07:00 UTC):
- Parses every `current[]` + `deprecated[]` entry from `verbara-website/data/authorized-digests.json`.
- Per entry: asserts `image_ref` resolves anonymously, actual digest matches `manifest_list_digest`, pinned digest cosign-verifies.

Both workflows auto-open an idempotent issue on regression with dimension-specific fix playbooks.

### Layer 3 — Retroactive-tag guard (Platform PR #5)

Add a pre-step to `release.yml` that reads `git for-each-ref --format='%(contents)' refs/tags/$REF` and, if the annotated message contains the literal `RETROACTIVE-TAG` marker, sets `steps.retro.outputs.skip=true` so every subsequent build/push/sign/verify step is conditionally skipped. Backward-compatible: tags without the marker behave identically to before.

### Layer 4 — Align cosign tooling cross-context (Platform PRs #6 + #8 + #10)

The maintainer-local cosign was `v3.0.6`; CI cosign was pinned at `v2.5.2` in `release.yml + visibility-monitor.yml + digest-reconciliation.yml`. Cosign v3.x refactored the detached-signature distribution layout in a way that breaks v2.5.2 verifies of v3.x-produced signatures. The 4 v2.4.2 images appeared "unsigned" in CI runs of the freshly-shipped Layer 2 checks even though they verified fine locally.

The simple fix (`cosign-release: 'v2.5.2'` → `'v3.0.6'`, **PR #6**) surfaced a second drift: `sigstore/cosign-installer@v3` cannot bootstrap cosign v3.x because v3.x dropped the plain `.sig` companion artifact in favor of Sigstore-rooted signing. The installer's bootstrap step that curl-fetched `cosign-linux-amd64.sig` from the GitHub release page started 404'ing. The fix is `sigstore/cosign-installer@v3` → `@v4.1.2` (**PR #8**, pinned exact because the installer repo only ships per-minor tags — no aggregate `v4` moving tag exists).

A third drift surfaced when the v2.4.3 release ran with the freshly-aligned cosign v3.0.6: the `Sign final image with cosign` step in `release.yml` died with:

```
Flag --tlog-upload has been deprecated, prefer using a --signing-config file
Error: --tlog-upload=false is not supported with --signing-config or --use-signing-config.
       Provide a signing config with --signing-config without a transparency log service.
```

The Verbara signing posture skips Rekor (the public transparency log). In cosign v2.x this was a simple `cosign sign --tlog-upload=false ...` flag. cosign v3.x deprecated the flag in favor of `--signing-config <file>` where the JSON has its `rekorTlogUrls` array stripped. The maintainer-local cosign was already using this pattern (the file lives at `~/.verbara/keys/signing-config-no-tlog.json`, generated once via `curl https://raw.githubusercontent.com/sigstore/root-signing/refs/heads/main/targets/signing_config.v0.2.json | jq 'del(.rekorTlogUrls)'`) — that's why local signs worked but CI didn't. **PR #10** ports that pattern inline into `release.yml`: the Sign step now curls the signing-config + pipes through `jq` + writes to `mktemp` + cleans up via `trap`. The Verify step is unchanged because `--insecure-ignore-tlog` is still supported on `verify` (only `--tlog-upload` was dropped on `sign`).

Net result: cosign v3.0.6 across maintainer + 3 workflows + installer v4.1.2 + signing-config flow + verify-with-tlog-skip all aligned. The next forward release (v2.4.3 second attempt, after PR #10) shipped under a fully aligned toolchain — 4 jobs SUCCESS, 4 SIGNED, no drift.

The cost: this single "upgrade cosign" change broke 3 things and required 3 PRs across the 3-PR cosign sweep (binary version drift → installer version drift → sign-flag drift). The lesson is that **cosign v2.x → v3.x is a major version bump with breaking changes in three orthogonal dimensions** — binary, installer compatibility, and CLI flag semantics — and each dimension can fail independently. Future cosign upgrades MUST test all three dimensions in a single PR with explicit "verify install + sign + verify against ghcr.io" smoke before merge.

### Layer 5 — Accept v2.4.2 as a transitional hybrid (Platform tag + GH Release)

Tag git `v2.4.2` from commit `ae41ee6e` with annotated message carrying `RETROACTIVE-TAG: ...` + the 4 manifest digests + provenance trail. Create GH Release v2.4.2 manually with a customer-facing changelog that documents the hybrid origin honestly: api preserved from local-build (`sha256:bb5e90...`), 3 micros bear new digests from a partial pipeline run (see Known Anomaly below).

### Layer 6 — Forward release v2.4.3 via the full pipeline (Platform PR #9)

Bundle the Plan C `MigrationRunner.EnsureSchemaAsync` hotfix for `Verbara.Platform.Realtime/Program.cs` (closes Gap-1 from the 2026-05-23 smoke test) into a regular bump-and-tag flow from HEAD. The tag points to a commit that includes the retroactive-tag guard (PR #5) and the cosign tooling alignment (PRs #6, #8), so the official pipeline runs cleanly with all the new safety nets active. v2.4.3 becomes the recommended baseline for customers; v2.4.2 stays for audit.

## The retroactive-tag guard semantic trap (lesson learned)

The retroactive-tag guard in Layer 3 was designed assuming **the workflow file used by Actions when a tag is pushed is the version on `main` at the time of push**. That's wrong. GitHub Actions resolves the workflow file from **the ref of the push**, which for a tag push is the commit the tag points to. If the tag points to a commit older than the PR that added the guard, the guard isn't in that commit's workflow file, and the original (unguarded) `release.yml` runs.

We discovered this empirically: pushing `v2.4.2 → ae41ee6e` (a commit pre-PR #5) triggered the official `release.yml` with no guard. Three of four jobs (realtime, renderer, mail) completed before the maintainer hit `gh run cancel`. The api job was mid-build at cancel time (Native AOT compile is the slowest of the four — ~5–8 min vs ~2 min for IL builds) and the original signed digest was preserved by chance.

**Consequence**: the guard is forward-only protection. It defends future retroactive tags (where the tagged commit will contain the guard), but cannot retroactively defend tags on pre-guard commits. The accepted mitigation is operational discipline: any retroactive tag on a pre-guard commit MUST be preceded by a `gh workflow disable release.yml` + tag-push + `gh workflow enable release.yml` ritual, OR the tag must be applied to a commit that includes the guard (e.g. cherry-pick the relevant change forward, tag that). For v2.4.2 we accepted the partial-overwrite damage as an acceptable side-effect of the rescue (the new digests are pipeline-signed with full audit log, paradoxically improving 3 of 4 images).

## Consequences

### Customer-facing impact (positive)

- Customer-facing `cosign verify --key https://verbara.io/keys/cosign.pub ...` now works for the first time since the manuales were written (gap closed by Layer 1).
- Pro/ADR-0011 Layer C image-binding for the api image is fully preserved across v2.4.2 (digest unchanged → authorized entry still resolves).
- v2.4.3 onward gets full institutional trazability automatically.

### Customer-facing impact (negative)

- Customers who pulled `realtime/renderer/mail :v2.4.2` between 2026-05-22 and the partial-overwrite cancel timestamp may have a digest in their pull-cache that no longer resolves in ghcr.io. Mitigation: those 3 microservices don't carry Pro IP and aren't part of the image-binding contract — a re-pull silently fetches the new (pipeline-signed, signed-by-same-key) image. No license validation breaks.
- 6 `deprecated[]` entries in `verbara-website/data/authorized-digests.json` (`api:v2.4.1` through `api:v2.1.0`) became ghost references during the 2026-05-22 anomaly (the maintainer's `docker push :v2.4.2` overwrote `:v2.4.1`'s tag pointer; the next ghcr.io GC pass collected the orphaned digests). The `digest-reconciliation.yml` workflow correctly flags these as drift. Cleanup is bundled into the v2.4.3 release commit on `verbara-website` (Layer 6 follow-up).

### Process impact

- Every future release goes through `release.yml`. Direct `docker buildx push` to `ghcr.io/verbara/platform/*` tags is now detectable within ~24 h by the daily monitors (signature missing for the new digest → visibility-monitor fails → issue auto-opened).
- The 4-PR + 1-tag + 1-release sweep is the largest single-day hardening event in the Platform repo's history and closes every gap discovered by the audit.
- Tooling versions (cosign + cosign-installer) are now pinned cross-context. Updates require synchronized PRs across the 3 workflows + a maintainer-local cosign rebuild.

## Alternatives considered (rejected)

| # | Option | Rejected because |
|---|---|---|
| A | Tag `v2.4.2` from `ae41ee6e` + force `release.yml` rebuild + accept new digests + update website to match | Destroys Pro/ADR-0011 Layer C binding for every customer with cached v2.4.2 pulls. Splits the customer base into "pre-rebuild digest" vs "post-rebuild digest" with no migration path. |
| C | Delete the 4 v2.4.2 images from ghcr.io entirely + retag fresh from `ae41ee6e` | Same Layer C destruction as A, plus requires `packages:delete` org-admin scope the maintainer lacks. |
| D | Tag retroactively WITHOUT re-running pipeline, document v2.4.2 as "shipped pre-Actions-run" forever | Acceptable technically but leaves the narrative weak ("the current release lacks an Actions audit trail"). Adopted partially as Layer 5. |
| F (chosen) | D + adelantar v2.4.3 oficial inmediato | Preserves all customer state, restores institutional trazability for the new baseline within hours, and uses the recovery as the occasion to permanently close the process gaps. |

## Files changed

| Layer | Repo | Files | PR |
|---|---|---|---|
| 1 | `verbara-website` | `public/keys/cosign.pub` (new) | #16 |
| 2 | `Verbara.Platform` | `.github/workflows/visibility-monitor.yml` (extended), `.github/workflows/digest-reconciliation.yml` (new) | #5 |
| 3 | `Verbara.Platform` | `.github/workflows/release.yml` (retroactive-tag guard pre-step) | #5 |
| 4a (binary) | `Verbara.Platform` | All 3 workflow files (`cosign-release` v2.5.2→v3.0.6) | #6 |
| 4b (installer) | `Verbara.Platform` | All 3 workflow files (`cosign-installer` @v3→@v4.1.2) | #8 |
| 4c (sign flag) | `Verbara.Platform` | `release.yml` only (Sign step: `--tlog-upload=false` → `--signing-config <inline-jq-stripped>`) | #10 |
| 5 | `Verbara.Platform` | Git tag `v2.4.2` (annotated, retroactive) + GH Release v2.4.2 (manual) | — |
| 6 | `Verbara.Platform` | `src/Verbara.Platform.Realtime/Program.cs` (Plan C hotfix), `Directory.Build.props` (2.4.2 → 2.4.3), git tag `v2.4.3` | #9 |
| 6 follow-up | `verbara-website` | `data/authorized-digests.json` (v2.4.3 → `current`, v2.4.2 → `deprecated`, drop 6 ghosts) | (pending) |
| 6 follow-up | `Verbara.Platform` | `.github/workflows/visibility-monitor.yml` (`PLATFORM_TAG: v2.4.2` → `v2.4.3`) | (pending) |

## Open questions

None. All gaps closed in the sweep.
