# ADR-0030: Cosign v3 release-signing posture — explicit `--signing-config` with Rekor stripped (cross-repo)

- **Status:** Accepted
- **Date:** 2026-06-24
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Extends:** [ADR-0024 §Layer 4c](0024-v242-shipping-anomaly-and-process-hardening.md) — promotes the inline `--signing-config` fix from a one-off Platform `release.yml` repair into a **cross-repo signing-posture invariant** that every image-signing `release.yml` MUST follow.
- **Cross-references:** [Pro/ADR-0011 (image-digest binding Layer C)](https://github.com/verbara/Verbara.Sdk.Pro/blob/main/docs/decisions/0011-image-digest-binding-in-license-keys.md), [ADR-0022 (Native AOT shipping path)](0022-platform-api-aot-shipping-path.md), `.github/workflows/release.yml` (Sign step), `.github/cosign.pub`, `verbara-website/public/keys/cosign.pub`

## Context

Verbara signs every shippable image with cosign and a self-managed keypair (`COSIGN_PRIVATE_KEY` / `.github/cosign.pub`), and **deliberately skips Rekor** — the public Sigstore transparency log. Verbara's images are signed with a long-lived offline key and verified against a committed public key (`cosign verify --key ... --insecure-ignore-tlog`); a public, append-only tlog entry is neither required by the verification contract nor desirable for a product whose closed-source Pro IP ships inside the image (ADR-0022). The signing posture is intentionally key-only, no transparency log.

In cosign **v2.x** this was a single flag: `cosign sign --tlog-upload=false ...`. Cosign **v3.x removed `--tlog-upload=false`** (the flag is deprecated on `sign` and erroring when combined with a signing-config). Cosign v3.x replaces it with signing-config discipline: skipping the tlog now requires an explicit `--signing-config <file>` whose `rekorTlogUrls` array is **absent/empty**. (`verify` is unaffected — `--insecure-ignore-tlog` is still supported, so the verify step did not change.)

ADR-0024 §Layer 4c first hit this when Platform's `release.yml` was aligned to cosign v3.0.6 and the Sign step died with:

```
Flag --tlog-upload has been deprecated, prefer using a --signing-config file
Error: --tlog-upload=false is not supported with --signing-config or --use-signing-config.
```

That was fixed inline in Platform's `release.yml`, but the fix was recorded only as a Platform-local layer of the v2.4.2 rescue. It was never elevated to an ecosystem invariant. The gap surfaced this release: **`Verbara.Platform.Web`'s `release.yml` had not been migrated** — it still carried the v2.x `--tlog-upload=false` form. The `v3.11.0-web` image **built successfully but failed at the cosign sign step**, leaving an unsigned tag until the posture was ported to Web (Web PR #135). An unsigned published tag also trips `visibility-monitor.yml`'s "signatures present" assertion (ADR-0024 Layer 2) and breaks the customer-facing `cosign verify` documented in the SMB manuales.

This is a **cross-repo concern**: any repo whose `release.yml` signs images (Platform today; Web; any future signing host) inherits the same v3.x breaking change and the same Rekor-skip requirement.

## Decision

Every Verbara `release.yml` that signs images with cosign v3.x MUST sign with an **explicit `--signing-config <file>` whose `rekorTlogUrls` is stripped**, generated inline at sign time from the public Sigstore signing-config and piped through `jq`. This is the single, canonical Rekor-skip pattern across all repos; the v2.x `--tlog-upload=false` form is forbidden under cosign v3.x.

The canonical Sign step (verbatim from Platform `release.yml`, to be mirrored in every signing repo):

```sh
SIGNING_CONFIG="$(mktemp -t signing-config.XXXXXX).json"
trap '... rm -f "$KEY_FILE" "$SIGNING_CONFIG"' EXIT
curl -fsSL https://raw.githubusercontent.com/sigstore/root-signing/refs/heads/main/targets/signing_config.v0.2.json \
  | jq 'del(.rekorTlogUrls)' > "$SIGNING_CONFIG"
cosign sign --key "$KEY_FILE" --yes --signing-config "$SIGNING_CONFIG" "$IMAGE_REF"
```

Companion invariants (carried with the pattern):

- **Verify is unchanged:** `cosign verify --key .github/cosign.pub --insecure-ignore-tlog "$IMAGE_REF"` (no signing-config; tlog skip via `--insecure-ignore-tlog`).
- **Toolchain pinned cross-context** (per ADR-0024 Layer 4): `sigstore/cosign-installer@v4.1.2` + `cosign-release: 'v3.0.6'` in `release.yml` + `visibility-monitor.yml` + `digest-reconciliation.yml`, matched by the maintainer-local cosign.
- **Sign step retries** to tolerate the ghcr push→HEAD manifest-propagation race (cosign signs by digest; a just-pushed manifest can briefly 404).
- The generated `SIGNING_CONFIG` file is `mktemp` + `umask 077` + `trap`-cleaned, alongside the key file.

When migrating a repo's signing posture (cosign v2→v3), all three ADR-0024 dimensions MUST be validated together in one PR: binary version, installer compatibility, and the sign-flag → signing-config change.

## Consequences

- **Positive:** one signing posture across all repos; new signing hosts copy the canonical block. Rekor stays skipped without relying on the removed `--tlog-upload` flag. The customer-facing `cosign verify --key https://verbara.io/keys/cosign.pub ...` contract is preserved (verify side untouched).
- **Negative / risk:** the Sign step depends on fetching `signing_config.v0.2.json` from the public `sigstore/root-signing` repo at CI time — a network/upstream-schema dependency. If Sigstore bumps the schema past v0.2 or moves the path, every signing repo's Sign step breaks simultaneously; pin/vendor the schema if that proves fragile.
- **Negative (root cause this release):** because the fix lived only as a Platform-local ADR-0024 layer, Web's `release.yml` drifted unmigrated and shipped an unsigned `v3.11.0-web` image until Web PR #135. This ADR exists to make the posture an explicit cross-repo invariant so the next signing repo does not repeat it.
- **Neutral / trade-off:** no public transparency-log entry for Verbara images by design — verification is key-only against the committed `cosign.pub`. Acceptable given the offline-key + closed-Pro-IP posture (ADR-0022); revisit only if a customer audit requires a public tlog.

## Alternatives considered

- **Keep `--tlog-upload=false` and pin cosign at v2.5.2 forever** — rejected: ADR-0024 already aligned the toolchain to v3.0.6 (the v2.5.2↔v3.x verify incompat was itself a cause of the v2.4.2 anomaly); reverting reintroduces that drift.
- **Adopt Rekor (upload to the public transparency log) instead of stripping it** — rejected: contradicts the deliberate key-only posture; adds a public, append-only record of every release with no benefit to the committed-key verification contract.
- **Leave the fix Platform-local (status quo)** — rejected: that is exactly what let Web drift and ship unsigned. The decision is cross-repo by nature.
