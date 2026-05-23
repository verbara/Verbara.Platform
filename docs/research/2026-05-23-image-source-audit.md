# Image source audit — compose + Helm + ghcr.io visibility

> **Date:** 2026-05-23
> **Scope:** verify what runs locally (compose) and in Talos (Helm) actually pulls from ghcr.io published artifacts vs. builds-from-source vs. mirrors. Surface inconsistencies + adoption blockers.
> **Status:** research / findings only. Each finding has a follow-up scoped at the bottom of the doc — none are executed here.

## TL;DR (4 findings, ordered by adoption-impact)

1. 🔴 **`api/realtime/renderer/mail` packages on ghcr.io are NOT publicly pullable.** Anonymous `docker pull ghcr.io/verbara/platform/api:v2.4.2` returns HTTP 404 — the package visibility is "private" in the GitHub UI. Only `ghcr.io/verbara/platform/web` is public. Every compose/manual we ship that references the four Native-AOT images implicitly requires the customer to authenticate to ghcr.io with a GitHub PAT first. ADR-0018 declared "repos PUBLIC" (2026-05-10) — the packages did not follow.
2. 🔴 **`docker-compose.production.yml` BUILDS from source.** The "production" compose file targets generic production deployments and was supposed to be the "use signed pre-built images" path. Today every service (api/realtime/renderer/mail/web) sets `build: { context: .., dockerfile: ... }` instead of `image: ghcr.io/...`. Operators who use this file get a from-scratch build chain on their host — not the signed Native-AOT images. This is a leak of build complexity into production.
3. 🟡 **Helm chart `values.yaml` tags are stale.** `api.image.tag` = `v2.3.1` (current shipped is v2.4.2), `web.image.tag` = `v3.1.2-web` (current shipped is v3.1.3-web), `realtime.image.tag` = `v0.1.0-rc` (was never a real release tag — placeholder from Phase A.1). Talos lab deploys override these via `--set` so the staleness is masked operationally, but it's wrong out of the box.
4. 🟡 **`docker-compose.reference-smb.yml` tag default still v2.4.1.** I bumped this in the previous session as part of the v2.4.1 cutover; v2.4.2 shipped this morning. The default `${PLATFORM_API_TAG:-v2.4.1}` needs to become `v2.4.2` once the release is taggedmaintainer-side, and tracking what the canonical "current customer-facing" tag is becomes a manual chore.

## Detailed audit

### A. Compose files

| File | Audience | api/realtime/renderer/mail source | web source | Verdict |
|------|----------|-----------------------------------|------------|---------|
| `docker/docker-compose.full.yml` | dev + loadtest | `build: ../` from source | `build: ../../Verbara.Platform.Web` | ✅ correct — dev iterates locally |
| `docker/docker-compose.reference-smb.yml` | SMB customer on-prem | `ghcr.io/verbara/platform/*:v2.4.1` | `ghcr.io/verbara/platform/web:v3.0.3-web` | 🟡 stale tags; **🔴 also requires PAT** because packages are private (see finding 1) |
| `docker/docker-compose.production.yml` | "production" generic | `build: ../` from source | `build: ../../Verbara.Platform.Web` | 🔴 should pull `ghcr.io/...` like reference-smb |
| `docker/demo/docker-compose.demo.yml` | demo bundle | `build: ../../` from source | `build: ../../../Verbara.Platform.Web` | ✅ correct — demo is reproducible-from-source by design |

### B. Helm chart (`infra/k8s/helm/platform/values.yaml`)

```yaml
api:    { image: { repository: ghcr.io/verbara/platform/api,      tag: "v2.3.1" } }
web:    { image: { repository: ghcr.io/verbara/platform/web,      tag: "v3.1.2-web" } }
realtime: { image: { repository: ghcr.io/verbara/platform/realtime, tag: "v0.1.0-rc" } }
```

The Talos lab's `r55-platform` install overrides `image.repository` to the local registry `192.168.122.1:5050/verbara-platform/{api,web}` via `--set` (because the lab does not have outbound ghcr.io pull credentials). For external Helm consumers — anyone who installs the chart without `--set` overrides — the chart pulls from ghcr.io at the stale tags AND with the same authentication problem as compose.

Templates that respect both digest + tag (with digest taking precedence) are in `platform-api-deployment.yaml:79` and `realtime-deployment.yaml:59`. The renderer + mail templates are NOT present in the chart yet (see Plan C). The `web-deployment.yaml:25` is plain `image: "{{ ...repository }}:{{ ...tag }}"`.

### C. ghcr.io package visibility (anonymous pull test)

Method: `curl -H "Accept: application/vnd.oci.image.index.v1+json" "https://ghcr.io/v2/verbara/platform/<pkg>/manifests/<tag>"` with an anonymous bearer token from `https://ghcr.io/token?service=ghcr.io&scope=repository:verbara/platform/<pkg>:pull`.

| Package | Anonymous pull | Notes |
|---------|----------------|-------|
| `verbara/platform/api`      | ❌ HTTP 404 | "private" package visibility — token issuance succeeds, manifest read denied |
| `verbara/platform/realtime` | ❌ HTTP 404 | idem |
| `verbara/platform/renderer` | ❌ HTTP 404 | idem |
| `verbara/platform/mail`     | ❌ HTTP 404 | idem |
| `verbara/platform/web`      | ✅ HTTP 200 | already "public" — customers can `docker pull` without auth |

`web` is one-off correct. The other four images need their visibility flipped to public in the GitHub UI (Packages → settings → change visibility). This is a one-click change per package and unblocks every customer-facing image reference we ship.

### D. Cosign signatures (anchor for "is this what we shipped")

All five packages have cosign signatures present (`.sig` artifacts on the registry). v2.4.2 four-image set verified clean today via the cosign verify loop with `--insecure-ignore-tlog` against `~/.verbara/keys/cosign.pub`. The Web v3.1.3-web is similarly signed by its own `release.yml` workflow (key shared with Platform repo for unified verification — confirmed in `Verbara.Platform.Web/.github/workflows/release.yml`).

## Follow-up work (each item is a separate scoped change — NOT executed in this audit)

1. **Flip api/realtime/renderer/mail to public visibility on ghcr.io.** Single maintainer action in the GitHub Packages UI. Validate with `curl https://ghcr.io/v2/verbara/platform/api/manifests/v2.4.2` returning HTTP 200 anonymously. ADR-0018 already authorized this; the packages just lagged.
2. **Rewrite `docker/docker-compose.production.yml`** to mirror `reference-smb.yml`'s `image:` pattern (pull tagged from ghcr.io). Keep build: blocks as a commented-out fallback for air-gapped customers. Same chart of services, just `build:` → `image:`. ~30 min, low risk.
3. **Bump Helm `values.yaml` defaults** to v2.4.2 (api/realtime/renderer/mail) + v3.1.3-web (web). Add the renderer + mail templates if they don't yet exist (Plan C flags this as Open Q2). Re-render with `helm template` to confirm no drift. ~1 hr.
4. **Re-version `reference-smb.yml`** default tags from v2.4.1 to v2.4.2 once the v2.4.2 git tag is cut. (`fe8a1938` is the v2.4.2 commit but the tag hasn't been pushed — Plan C Open Q4.)
5. **Establish a "current customer-facing tag" automation.** A single source of truth file like `docs/CURRENT_RELEASE.md` (or a JSON manifest) that release commits bump in lockstep with `Directory.Build.props`. Compose files / Helm values / manuales / customer scripts grep that file at install time. Eliminates the manual "did we bump every reference" drift. Future plan.

## Cross-reference: Platform.Web image plan

A separate plan in the `Verbara.Platform.Web` repo addresses image-side improvements specific to that artifact (multi-arch build for ARM customers, latest-tag alias, install guide reference). See [`Verbara.Platform.Web/docs/plans/active/2026-05-23-web-image-adoption.md`](/media/Data/Source/Verbara/Verbara.Platform.Web/docs/plans/active/2026-05-23-web-image-adoption.md).
