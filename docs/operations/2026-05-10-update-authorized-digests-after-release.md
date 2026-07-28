# Operator runbook — Update `authorized-digests.json` after a Platform release

**Created:** 2026-05-10
**Owner:** Verbara maintainer
**Related:**
- [Pro v2.3.x execution plan §3.4](../../../Verbara.Sdk.Pro/docs/plans/completed/2026-05-09-pro-v23x-image-binding-execution.md)
- [ADR-0011 Image-digest binding in license keys](../../../Verbara.Sdk.Pro/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Verbara.Platform `.github/workflows/release.yml`](../../.github/workflows/release.yml)
- [Verbara website registry: `data/authorized-digests.json`](https://github.com/verbara/verbara-website) (private)

## When to run this

After **every** Verbara.Platform tagged release.

> **TWO entries per release — `api` AND `realtime`.**
> `data/authorized-digests.json` stores **one `current[]` entry per image per
> version**, and every release since **v2.5.1** is an `api` + `realtime` **pair**
> (v2.4.2/v2.4.3 were the last api-only entries). Record both: the realtime digest
> lands in every newly issued license's `AuthorizedImageDigests` claim, keeps
> `.github/workflows/digest-reconciliation.yml`'s drift sweep covering the realtime
> image, and keeps the allow-list forward-compatible.
>
> **What recording both does NOT do today: gate the realtime pod.** The Helm chart
> injects `IMAGE_DIGEST` on the **api** Deployment only; `realtime-deployment.yaml`
> uses `realtime.image.digest` for kubelet pinning (Layer B) and sets no
> `IMAGE_DIGEST`, and no image writes the `/etc/verbara-image-digest` sentinel — so
> `ContainerImageDigest.ReadFromEnvironment()` returns null inside realtime and
> `LicenseValidator` takes its documented permissive path. Layer C is api-only at
> runtime today; the realtime entry is allow-list hygiene and future-proofing.
>
> `renderer` and `mail` carry no Pro licensing code and are **not** image-bound —
> do not add entries for them.

`.github/workflows/release.yml` builds a 4-image matrix and its
"Authorized-digests reminder (api + realtime)" step fires on **both** image-bound
legs. Each leg prefixes every line with its own image so the two digests can
never be confused:

```
[api] Next step — Layer-C image-binding for the 'api' image:
[api] This leg covers the 'api' entry ONLY. The 'realtime' leg of this
[api] same matrix prints its own, DIFFERENT digest — this release needs BOTH.
[api]   platform_version     = vX.Y.Z
[api]   image_ref            = ghcr.io/verbara/platform/api:vX.Y.Z
[api]   manifest_list_digest = sha256:abcd...
[api]   released_at          = 2026-MM-DDTHH:MM:SSZ
```

**Read the run's Step Summary page, not the job logs.** Both legs append a
ready-to-paste JSON block to the *same* summary, so the full `api` + `realtime`
pair is on one page — you do not have to open the second matrix job. Both blocks
carry the **same** `released_at` — one timestamp per version is the convention every
existing entry follows (verified: 52 entries / 27 versions, zero disagreements). The
VALUE, however, is new: from v2.22.0 it is derived from the **tag**, whereas every
entry recorded before that used the release run's completion time (v2.21.2's tag dates
`2026-07-26T19:17:04Z`; its recorded `released_at` is `2026-07-27T01:03:41Z`). Where the
tag is *lightweight* (e.g. v2.20.0, v2.21.2) `%(creatordate)` is the tagged commit's
date, which can be hours earlier than the publish. Before pasting, confirm the value
still sorts AFTER the previous release's `released_at`; if the tag was cut materially
earlier than it was pushed, overwrite it on BOTH entries with the run's completion time.
`current` is sorted by `released_at` DESC and truncated at `MAX_EMBEDDED_DIGESTS`, so an
artificially early stamp can evict the newest digest from freshly issued licenses.

Until **both** entries land in `verbara-website` and the Worker is redeployed,
**newly issued Pro licenses will NOT include the new image digests**, which
means the api image is NOT covered by those licenses. Two outcomes, depending on the
operator's posture: an api pod deployed WITHOUT `api.image.digest` has no readable
digest, so `ContainerImageDigest.ReadFromEnvironment()` returns null and validation
falls through permissively; an api pod deployed digest-pinned (the recommended Layer-B
posture, which also injects `IMAGE_DIGEST`) has a readable digest that is absent from
the license's non-empty allow-list, and `LicenseValidator` returns **`UnauthorizedImage`**
— the customer is BLOCKED, not merely unguarded. Realtime is unaffected either way (no
digest is readable there). Layer B (cosign admission policy) still works because it
verifies the signature directly, not the license.

Run this runbook within 24h of every release to keep the issuer in sync.

## Step 1 — Capture BOTH manifest-list digests (`api` + `realtime`)

The release workflow's Step Summary page has both, ready to paste. You can also
re-derive them after the fact — run each command **twice**, once per image:

```sh
# requires `crane` (https://github.com/google/go-containerregistry):
crane digest ghcr.io/verbara/platform/api:vX.Y.Z
crane digest ghcr.io/verbara/platform/realtime:vX.Y.Z

# OR via cosign:
cosign triangulate ghcr.io/verbara/platform/api:vX.Y.Z
cosign triangulate ghcr.io/verbara/platform/realtime:vX.Y.Z

# OR via docker buildx:
for img in api realtime; do
  docker buildx imagetools inspect "ghcr.io/verbara/platform/${img}:vX.Y.Z" \
    --format '{{.Manifest.Digest}}'
done
```

All three approaches return the same `sha256:...` value per image: the OCI
manifest-list digest. **These are the digests you will record** — NOT a
per-arch digest (per-arch digests are explicitly rejected; see Pro/ADR-0011
§ "Multi-arch + manifest-list digest semantics").

The `api` and `realtime` digests are **always different values**. If you have
copied the same `sha256:` twice, you have made the mistake this step exists to
prevent.

## Step 2 — PR **both** new entries into `verbara-website`

Clone `github.com/verbara/verbara-website` and edit
`data/authorized-digests.json`. Append **two** objects to the `current`
array — one per image-bound image, sharing the same `platform_version` and
`released_at`:

```diff
 {
   "$schema": "https://verbara.io/schemas/authorized-digests-v1.json",
   "current": [
+    {
+      "platform_version": "vX.Y.Z",
+      "image_ref": "ghcr.io/verbara/platform/api:vX.Y.Z",
+      "manifest_list_digest": "sha256:abcd...",
+      "released_at": "2026-MM-DDT00:00:00Z"
+    },
+    {
+      "platform_version": "vX.Y.Z",
+      "image_ref": "ghcr.io/verbara/platform/realtime:vX.Y.Z",
+      "manifest_list_digest": "sha256:ef01...",
+      "released_at": "2026-MM-DDT00:00:00Z"
+    }
   ],
   "deprecated": []
 }
```

Validation rules enforced by the schema (see
`verbara-website/data/README.md`):

- `manifest_list_digest` MUST match `sha256:<64 lowercase hex>` — the exact shape
  `verbara-website/scripts/validate-authorized-digests.mjs` enforces (`DIGEST_RE`,
  `/^sha256:[0-9a-f]{64}$/`) and the shape Pro v2.3.x's `LicenseReader.Load`
  parse-time validator accepts (a malformed digest raises `LicenseException`)
- `image_ref` MUST be the `ghcr.io/verbara/platform/<api|realtime>:vX.Y.Z` form (NOT
  `@sha256:...` form — operators read this as a human-friendly reference,
  the digest is the load-bearing field)
- `released_at` MUST be ISO-8601 UTC (`Z` suffix)

Before opening the PR, run the repo's structural guard:

```sh
npm run validate:digests   # scripts/validate-authorized-digests.mjs
```

**Rotation arithmetic — count ENTRIES, not releases.** The Worker embeds the
**last 6 `current` entries** sorted by `released_at` DESC
(`MAX_EMBEDDED_DIGESTS` in `functions/api/developer-license/authorized-digests.ts`),
per the Pro/ADR-0011 § "Issuer rotation cadence". Because each release contributes a
**pair**, 6 entries is **3 releases** of headroom, not 6. After 3 future releases the
older entries become eligible to move to `deprecated` — a separate operator decision,
not required for each release. Always move a version's `api` and `realtime` entries
**together**; splitting a pair authorizes half a deployment.

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
returned `.lic` file contains **both** new digests in `AuthorizedImageDigests`:

```sh
# Decode the .lic JSON payload (it's base64-encoded JSON):
jq -r '.payload' < developer.lic | base64 -d | jq '.AuthorizedImageDigests'
# The claim is a flat array of bare digest strings (no image labels). Expect BOTH
# of this release's digests to be present, most recent first:
#   ["sha256:abcd...", "sha256:ef01...", "sha256:older...", ...]
#      ^ api            ^ realtime
```

If only one of the two shows up, the pair was recorded incomplete — go back to
Step 2 before deploying.

## Step 4 — Smoke-test the new images with the fresh license

```sh
for img in api realtime; do
  # Pull the new image
  docker pull "ghcr.io/verbara/platform/${img}:vX.Y.Z"

  # Verify signature (out-of-band)
  cosign verify \
    --key https://verbara.io/.well-known/cosign.pub \
    --insecure-ignore-tlog \
    "ghcr.io/verbara/platform/${img}:vX.Y.Z"
done

# Run the api image with the fresh license + verify Pro features unlock
docker run --rm \
  -e "VERBARA_LICENSE_PATH=/etc/verbara/developer.lic" \
  -v /path/to/developer.lic:/etc/verbara/developer.lic:ro \
  ghcr.io/verbara/platform/api:vX.Y.Z
```

Confirm the OTel meter `Verbara.Sdk.Pro.Licensing.Guard` is NOT emitting
`verbara.licensing.image_unauthorized` events (Layer C check passes
because the new digest is in the license's allow-list). Repeat against the
`realtime` image if the deployment pins it by digest
(`realtime.image.digest` in `infra/k8s/helm/platform/values.yaml`).

## Future automation (not in v2.3.x scope)

The minimum-viable flow is the manual PR + merge above. Future automation
is tracked separately:

- Since **v2.22.0**, `release.yml`'s reminder step fires on both image-bound
  matrix legs and writes a paste-ready JSON block per image into the run's
  Step Summary — the manual transcription is down to a copy/paste of two
  blocks off one page. The remaining automation would be to post the
  `verbara-website` PR itself after the cosign sign step, merging
  automatically if a bot account with verbara-website write permissions is
  configured. **Any such automation must emit the api+realtime PAIR**, not a
  single entry.
- A drift-detection cron in `verbara-website` already runs daily and
  HEADs each `image_ref` in `current` to confirm the registry digest
  still matches the recorded `manifest_list_digest`. Note it can only detect
  drift in entries that EXIST — a missing `realtime` entry is invisible to it.
  If the maintainer forgets to run this runbook, the cron emails
  `security@verbara.io` on the next drift sweep — but the issuer Worker keeps
  issuing licenses with stale digests until the runbook is run.

For a customer who upgrades past the rotation window (6 `current` entries =
**3 releases**, since each release contributes an api+realtime pair) without
re-fetching their license, see the Pro/ADR-0011 § "Issuer rotation cadence"
discussion of the `/api/developer-license/refresh` endpoint (TBD; not
implemented yet).
