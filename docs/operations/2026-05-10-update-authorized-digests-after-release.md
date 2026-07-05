# Operator runbook — Update `authorized-digests.json` after a Platform release

**Created:** 2026-05-10
**Owner:** Verbara maintainer
**Related:**
- [Pro v2.3.x execution plan §3.4](../../../Verbara.Sdk.Pro/docs/plans/completed/2026-05-09-pro-v23x-image-binding-execution.md)
- [ADR-0011 Image-digest binding in license keys](../../../Verbara.Sdk.Pro/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Verbara.Platform `.github/workflows/release.yml`](../../.github/workflows/release.yml)
- [Verbara website registry: `data/authorized-digests.json`](https://github.com/verbara/verbara-website) (private)

## When to run this

After **every** Verbara.Platform tagged release that publishes a signed
image to `ghcr.io/verbara/platform/api`. The release workflow ends with:

```
Signed image manifest-list digest: sha256:abcd...
Final image reference: ghcr.io/verbara/platform/api@sha256:abcd...
Next step: append a new entry to verbara-website's data/authorized-digests.json
```

Until that entry lands in `verbara-website` and the Worker is redeployed,
**newly issued Pro licenses will NOT include the new image digest**, which
means customers running the new image will fall through to the permissive
path (Layer C disabled because their license has no matching digest in
its allow-list). Layer B (cosign admission policy) still works because
it verifies the signature directly, not the license.

Run this runbook within 24h of every release to keep the issuer in sync.

## Step 1 — Capture the new image's manifest-list digest

The release workflow emits the digest in its final step. You can also
re-derive it after the fact:

```sh
# requires `crane` (https://github.com/google/go-containerregistry):
crane digest ghcr.io/verbara/platform/api:vX.Y.Z

# OR via cosign:
cosign triangulate ghcr.io/verbara/platform/api:vX.Y.Z

# OR via docker buildx:
docker buildx imagetools inspect ghcr.io/verbara/platform/api:vX.Y.Z \
  --format '{{.Manifest.Digest}}'
```

All three approaches return the same `sha256:...` value: the OCI
manifest-list digest. **This is the digest you will record** — NOT a
per-arch digest (per-arch digests are explicitly rejected; see Pro/ADR-0011
§ "Multi-arch + manifest-list digest semantics").

## Step 2 — PR the new entry into `verbara-website`

Clone `github.com/verbara/verbara-website` and edit
`data/authorized-digests.json`. Append a new object to the `current`
array:

```diff
 {
   "$schema": "https://verbara.io/schemas/authorized-digests-v1.json",
   "current": [
+    {
+      "platform_version": "vX.Y.Z",
+      "image_ref": "ghcr.io/verbara/platform/api:vX.Y.Z",
+      "manifest_list_digest": "sha256:abcd...",
+      "released_at": "2026-MM-DDT00:00:00Z"
+    }
   ],
   "deprecated": []
 }
```

Validation rules enforced by the schema (see
`verbara-website/data/README.md`):

- `manifest_list_digest` MUST start with `sha256:` or `sha512:` (Pro v2.3.x's
  `LicenseReader.Load` parse-time validator rejects malformed digests with
  a `LicenseException`)
- `image_ref` MUST be the `ghcr.io/verbara/platform/api:vX.Y.Z` form (NOT
  `@sha256:...` form — operators read this as a human-friendly reference,
  the digest is the load-bearing field)
- `released_at` MUST be ISO-8601 UTC (`Z` suffix)

The Worker only embeds the **last 6 entries** from `current` in newly
issued licenses (rotation cadence per Pro/ADR-0011 § "Issuer rotation
cadence"). After 6 future releases, the older entries become eligible
to move to `deprecated` — that is a separate operator decision, not
required for each release.

## Step 3 — Merge the PR + redeploy the Worker

`verbara-website` Cloudflare Worker auto-deploys on merge to `main` IF
git auto-deploy is reconnected. As of 2026-05-09 it was detached after
manual `wrangler versions secret put`. Workaround until reconnected:

```sh
cd /path/to/verbara-website
npm run deploy
```

Verify the deploy:

```sh
curl https://verbara.io/api/developer-license/probe
# Should respond 200 with the deploy timestamp post-merge.
```

Then issue a fresh test license through the public form and confirm the
returned `.lic` file contains the new digest in `AuthorizedImageDigests`:

```sh
# Decode the .lic JSON payload (it's base64-encoded JSON):
jq -r '.payload' < developer.lic | base64 -d | jq '.AuthorizedImageDigests'
# Expect: ["sha256:abcd...", "sha256:older...", ...]
```

## Step 4 — Smoke-test the new image with the fresh license

```sh
# Pull the new image
docker pull ghcr.io/verbara/platform/api:vX.Y.Z

# Verify signature (out-of-band)
cosign verify \
  --key https://verbara.io/.well-known/cosign.pub \
  --insecure-ignore-tlog \
  ghcr.io/verbara/platform/api:vX.Y.Z

# Run with the fresh license + verify Pro features unlock
docker run --rm \
  -e "VERBARA_LICENSE_PATH=/etc/verbara/developer.lic" \
  -v /path/to/developer.lic:/etc/verbara/developer.lic:ro \
  ghcr.io/verbara/platform/api:vX.Y.Z
```

Confirm the OTel meter `Verbara.Sdk.Pro.Licensing.Guard` is NOT emitting
`verbara.licensing.image_unauthorized` events (Layer C check passes
because the new digest is in the license's allow-list).

## Future automation (not in v2.3.x scope)

The minimum-viable flow is the manual PR + merge above. Future automation
is tracked separately:

- A GitHub Action in `Verbara.Platform/.github/workflows/release.yml`
  could, after the cosign sign step, post a PR against `verbara-website`
  with the new digest appended. That PR could merge automatically if a
  bot account with verbara-website write permissions is configured.
- A drift-detection cron in `verbara-website` already runs daily and
  HEADs each `image_ref` in `current` to confirm the registry digest
  still matches the recorded `manifest_list_digest`. If the maintainer
  forgets to run this runbook, the cron emails `security@verbara.io`
  on the next drift sweep — but the issuer Worker keeps issuing licenses
  with stale digests until the runbook is run.

For a customer who upgrades past the 6-patch rotation window without
re-fetching their license, see the Pro/ADR-0011 § "Issuer rotation cadence"
discussion of the `/api/developer-license/refresh` endpoint (TBD; not
implemented yet).
