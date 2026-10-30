# Operator runbook — Update `authorized-digests.json` after a Platform release

**Created:** 2026-05-10
**Owner:** Verbara maintainer
**Related:**
- [Pro v2.3.x execution plan §3.4](../../../Verbara.Sdk.Pro/docs/plans/completed/2026-05-09-pro-v23x-image-binding-execution.md)
- [ADR-0011 Image-digest binding in license keys](../../../Verbara.Sdk.Pro/docs/decisions/0011-image-digest-binding-in-license-keys.md)
- [Verbara.Platform `.github/workflows/release.yml`](../../.github/workflows/release.yml)
- [Verbara website registry: `data/authorized-digests.json`](https://github.com/verbara/verbara-website) (private)

> **Status 2026-10-29 — the ledger is no longer embedded in licences.** Since
> 2026-10-03 no licence carries `AuthorizedImageDigests`: the free Developer
> licence (Tier 0.5) omits the field (verbara-website #122) and Tier 2/5
> issuance omits it too (verbara-meta/PDR-0003, amendment of 2026-10-03; Pro
> ADR-0011 status note, Verbara.Sdk.Pro #277). `data/authorized-digests.json`
> stays the **release ledger**: the daily drift sweep
> (`.github/workflows/digest-reconciliation.yml` and the verbara-website cron)
> re-verifies every entry, and operators pin those digests in the Helm chart
> (`api.image.digest` / `realtime.image.digest`) and the compose files. Keep
> running this runbook after every release; the steps below that checked the
> digests inside an issued `.lic` were removed.

## When to run this

After **every** Verbara.Platform tagged release.

> **Stop condition — do not run this while that tag's Release run is not green.** If the
> release's Release workflow run shows any `Build, push, and sign` job as failed, or its
> `Post-release functional smoke` job as failed **or skipped**, do **not** register the
> `api` + `realtime` digests: registering records an unverified image set as a Verbara
> release, and operators pin what the ledger lists. A
> skipped smoke is not a pass: when one image fails the provenance gate before its push,
> the smoke is skipped while the passing `api` and `realtime` legs still print their
> digest blocks. Re-run or fix the release first; register only once every release job
> and the smoke are green.
> (A retroactive tag also skips the smoke, by design: that run builds and publishes
> nothing, so this runbook does not apply to it.)

> **TWO entries per release — `api` AND `realtime`.**
> `data/authorized-digests.json` stores **one `current[]` entry per image per
> version**, and every release since **v2.5.1** is an `api` + `realtime` **pair**
> (v2.4.2/v2.4.3 were the last api-only entries). Record both: the realtime entry
> keeps `.github/workflows/digest-reconciliation.yml`'s drift sweep covering the
> realtime image, and it is the published digest operators pin in
> `realtime.image.digest`.
>
> `renderer` and `mail` carry no Pro licensing code and get no ledger entry —
> do not add entries for them.

`.github/workflows/release.yml` builds a 4-image matrix and its
"Authorized-digests reminder (api + realtime)" step fires on **both** ledger
legs. Each leg prefixes every line with its own image so the two digests can
never be confused:

```
[api] Next step — release-ledger entry for the 'api' image:
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
`current` is kept sorted by `released_at` DESC, so an artificially early stamp
misorders the ledger.

Until **both** entries land in `verbara-website`, the drift sweep does not cover
the new images and the ledger does not list the digests operators are told to pin.
Licences are unaffected: none carries image digests, so no licence has to be
reissued for a new release.

Run this runbook within 24h of every release to keep the ledger in sync.

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
array — one per ledger image (api, realtime), sharing the same `platform_version` and
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
  `/^sha256:[0-9a-f]{64}$/`)
- `image_ref` MUST be the `ghcr.io/verbara/platform/<api|realtime>:vX.Y.Z` form (NOT
  `@sha256:...` form — operators read this as a human-friendly reference,
  the digest is the load-bearing field)
- `released_at` MUST be ISO-8601 UTC (`Z` suffix)

Before opening the PR, run the repo's structural guard:

```sh
npm run validate:digests   # scripts/validate-authorized-digests.mjs
```

**Moving entries to `deprecated`** is a separate operator decision, not required for
each release; nothing reads a fixed number of `current` entries any more. Always move a
version's `api` and `realtime` entries **together**; splitting a pair leaves half a
deployment out of `current`.

## Step 3 — Merge the PR + redeploy the Worker

`verbara-website` Cloudflare Worker auto-deploys on merge to `main` IF
git auto-deploy is reconnected. As of 2026-05-09 it was detached after
manual `wrangler versions secret put`. Workaround until reconnected:

```sh
cd /path/to/verbara-website
npm run deploy
```

The Worker bundles the ledger for its daily drift-detection cron, so the
redeploy is what puts the new pair under that sweep.

Before deploying, confirm the PR added **both** entries (`api` and
`realtime`, same `platform_version`, different digests). If only one is
there, the pair was recorded incomplete — go back to Step 2.

## Step 4 — Smoke-test the new images with a current license

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

# Run the api image with a current license + verify Pro features unlock
docker run --rm \
  -e "VERBARA_LICENSE_PATH=/etc/verbara/developer.lic" \
  -v /path/to/developer.lic:/etc/verbara/developer.lic:ro \
  ghcr.io/verbara/platform/api:vX.Y.Z
```

Confirm the Pro endpoints answer with the licence loaded (no 402) and the
signatures verified for both images.

## Future automation (not in v2.3.x scope)

The minimum-viable flow is the manual PR + merge above. Future automation
is tracked separately:

- Since **v2.22.0**, `release.yml`'s reminder step fires on both the api and the realtime
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
  If the maintainer forgets to run this runbook, the new release's images
  stay outside the sweep until the runbook is run.

A customer holding a licence issued before 2026-10-03 may have image digests
in it; such a licence keeps being checked against the running api image, so
that customer needs a current licence from Verbara to upgrade. Licences issued
since carry no digests and need nothing on upgrade.
