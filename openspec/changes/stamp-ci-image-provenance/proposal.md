---
tier: PEQUENO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Whoever has to answer "which commit is this running container built from?"
decision_ref: Platform/ADR-0022
---

## Why

`canonicalize-rbac-permission-vocabulary` (#225) added `org.opencontainers.image.*` labels to the
root `Dockerfile` because the question that started that investigation — *is the lab actually
running the code we think it is?* — could not be answered from image metadata. Before the change
`docker inspect` on a shipped API image showed only the Ubuntu base's inherited labels
(`image.version=24.04`); nothing tied the image to a commit.

The labels landed, but only half the goal did. **`release.yml` passes no `build-args`** to its
`docker/build-push-action` step, so every image CI publishes carries
`revision` / `created` / `version` = `unknown`. Only a local build with the variables exported gets
real values. The gap was recorded as Out of Scope in #225 and is tracked here so it does not vanish
into the archive.

Two smaller pieces of the same gap:

- **The three sibling images have no labels at all.** The release matrix builds `api`, `realtime`,
  `renderer` and `mail`; only the API's `Dockerfile` was touched by #225. The other three carry
  nothing to stamp even once the workflow passes the arguments.
- **Nothing verifies the labels.** A build-arg that stops being passed — or is renamed — degrades
  silently back to `unknown`, which is exactly how the current state reads as "fine" until someone
  inspects an image.

This is small and unglamorous, and it is the difference between diagnosing a stale deployment in one
command and diagnosing it by rebuilding.

## What Changes

- **Pass the provenance build-args in `release.yml`** — `VCS_REF`, `BUILD_DATE` and `VERSION`,
  sourced from the workflow's own commit/tag context, so published images carry real values.
- **Add the same `ARG` + `LABEL` block to the three sibling Dockerfiles**
  (`Dockerfile.realtime`, `Dockerfile.renderer`, `Dockerfile.mail`) so the whole matrix is
  traceable, not just the API.
- **Assert the labels after the build** — a release-workflow step that inspects the built image and
  fails if `org.opencontainers.image.revision` is missing or `unknown`, so the arguments cannot
  quietly stop being passed.

## Capabilities

### New Capabilities

- `container-image-provenance`: every image this repo publishes is traceable from `docker inspect`
  back to the commit, timestamp and version it was built from, and the traceability is verified at
  build time rather than assumed.

### Modified Capabilities

<!-- None. No existing capability owns the release workflow's image metadata. -->

## Impact

- **Workflow:** `.github/workflows/release.yml` — the `Build and push final image` step gains
  `build-args`, and the job gains a post-build label assertion. The step already passes a BuildKit
  `secret`; `build-args` is additive and unrelated to it.
- **Dockerfiles:** `src/Verbara.Platform.Realtime/Dockerfile.realtime`,
  `src/Verbara.Platform.Renderer/Dockerfile.renderer`,
  `src/Verbara.Platform.Mail/Dockerfile.mail` — the `ARG`/`LABEL` block the root `Dockerfile`
  already carries. The root `Dockerfile` itself needs no change.
- **Data / runtime:** none. Labels are metadata; nothing reads them at runtime. In particular this
  is unrelated to `IMAGE_DIGEST`, which Pro's Layer C license check reads from the environment.
- **Cross-repo:** none here. `Verbara.Platform.Web` publishes its own image from its own repo and
  would need the same treatment separately.

## Architectural Risk

Low. The change is metadata plus a workflow assertion, and a wrong label cannot affect a running
container. The one real hazard is the assertion itself: a check that fails the release job on
missing provenance can block a legitimate release if it is written against a label the build does
not actually produce — so it must be verified against a real build before it becomes a gate, not
merged on the assumption that it passes.

Worth stating plainly: this closes a *diagnostic* gap, not a security one. Labels are attacker-
controllable metadata on an untrusted image and are not an integrity mechanism; image signing and
digest pinning remain the mechanisms that answer "is this image the one we published".

### Out of Scope (explicit)

- **Image signing and digest pinning.** Already covered elsewhere (cosign in the release flow, and
  Pro's Layer C digest check via `IMAGE_DIGEST`); this change is about human-readable provenance.
- **`Verbara.Platform.Web`'s image**, which is built and published from its own repository.
- **Backfilling labels onto already-published images.** Not possible without a rebuild, and not
  worth one; the labels start being useful from the next release.
