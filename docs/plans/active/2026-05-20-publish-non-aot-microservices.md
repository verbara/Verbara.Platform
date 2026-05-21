# Plan — Publish the non-AOT microservices (Realtime / Renderer / Mail)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.
> Steps use checkbox (`- [ ]`) syntax.

**Goal:** Publish `realtime`, `renderer`, `mail` as **public, cosign-signed** images to
`ghcr.io/verbara/platform/*`, versioned with the Platform tag, with a build guard that keeps
crown-jewel Pro IP out of these IL images.

**Spec / decision:** [ADR-0023](../../decisions/0023-publishing-non-aot-microservices.md) ·
[risk analysis](../../research/2026-05-20-microservices-publishing-ip-risk.md)

**Architecture:** Extend the existing single-image `release.yml` into a **matrix** that builds
all 4 images (api AOT + 3 IL microservices) on the same `v*` Platform tag, each pushed to its
own `ghcr.io/verbara/platform/<name>` repo and cosign-signed. Images inherit public visibility.

---

## Task 1 — Crown-jewel Pro guard (foundation, do first)

**Files:** Modify `src/Verbara.Platform.Realtime/Verbara.Platform.Realtime.csproj`,
`src/Verbara.Platform.Renderer/Verbara.Platform.Renderer.csproj`,
`src/Verbara.Platform.Mail/Verbara.Platform.Mail.csproj` — OR a shared import.

- [ ] **Step 1:** Add a shared MSBuild target file `build/BanCrownJewelPro.targets`:

```xml
<Project>
  <!-- ADR-0023: non-AOT microservices ship as IL; they MUST NOT carry crown-jewel
       Pro IP. Build fails if a crown-jewel Pro package is referenced. -->
  <Target Name="BanCrownJewelProInNonAotMicroservices" BeforeTargets="CollectPackageReferences;BeforeCompile">
    <ItemGroup>
      <_CrownJewel Include="@(PackageReference)" Condition="
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.Dialer')) Or
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.Analytics')) Or
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.CallAnalytics')) Or
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.AgentAssist')) Or
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.EventStore')) Or
        $([System.String]::Copy('%(PackageReference.Identity)').StartsWith('Verbara.Sdk.Pro.Routing'))" />
    </ItemGroup>
    <Error Condition="'@(_CrownJewel)' != ''"
           Text="ADR-0023: '@(_CrownJewel)' is a crown-jewel Pro package and MUST NOT ship as decompilable IL in the non-AOT microservice $(MSBuildProjectName). Keep that logic in the AOT API." />
  </Target>
</Project>
```

- [ ] **Step 2:** Import it in the 3 microservice csproj (`<Import Project="..\..\build\BanCrownJewelPro.targets" />`).
- [ ] **Step 3:** Build all 3 → expect success (none reference crown jewels today).
- [ ] **Step 4:** Negative test — temporarily add `Pro.Dialer` to Realtime → expect the ADR-0023 error → revert.
- [ ] **Step 5:** Commit.

## Task 2 — Modernize Renderer + Mail Dockerfiles

**Files:** `src/Verbara.Platform.Renderer/Dockerfile.renderer`, `src/Verbara.Platform.Mail/Dockerfile.mail`

- [ ] **Step 1:** Remove the stale `/media/Data/Source/IPcom/local-nuget-feed/` sed + local-feed
  branch. These projects have **no Pro deps**, so restore from **nuget.org only** (SDK 2.2.0 is
  published). Mirror the api Dockerfile's `dotnet nuget remove source local` pattern (no github auth needed).
- [ ] **Step 2:** Local build each image (`docker build -f Dockerfile.renderer ..`) to confirm clean restore.
- [ ] **Step 3:** Commit.

## Task 3 — Extend release.yml to a 4-image matrix

**Files:** `.github/workflows/release.yml`

- [ ] **Step 1:** Add a build matrix:
  - `api` → `Dockerfile`, AOT (existing args), needs `nuget_auth_token` (Pro).
  - `realtime` → `src/Verbara.Platform.Realtime/Dockerfile.realtime`, needs `nuget_auth_token` (Pro).
  - `renderer` → `src/Verbara.Platform.Renderer/Dockerfile.renderer`, no token.
  - `mail` → `src/Verbara.Platform.Mail/Dockerfile.mail`, no token.
- [ ] **Step 2:** Each pushes `ghcr.io/verbara/platform/<name>:${release_tag}` and is cosign-signed
  (reuse the existing COSIGN_PRIVATE_KEY/PASSWORD step per image digest).
- [ ] **Step 3:** Keep `IMAGE_NAME` per-matrix-entry; preserve the api's existing build-args.
- [ ] **Step 4:** Validate the workflow YAML (`actionlint` or a dry parse).
- [ ] **Step 5:** Commit.

## Task 4 — Ensure ghcr package visibility = public

- [ ] **Step 1:** After first publish, set `realtime`/`renderer`/`mail` container packages to
  **public** in the org package settings (or via API). The api is already public; match it.
- [ ] **Step 2:** Verify anonymous pull works (`docker pull` logged out, or anonymous-token manifest probe).

## Task 5 — Wire compose files to the published images (optional, follow-up)

- [ ] **Step 1:** Offer overrides so `docker-compose.full.yml` / `reference-smb` can use
  `image: ghcr.io/verbara/platform/<name>:vX` instead of `build:` (trial-friendly `compose up`).
  Keep `build:` available for local dev. (Lower priority; can ship after the workflow.)

## Task 6 — Release + verify

- [ ] **Step 1:** Tag a Platform release (e.g., next `v2.4.x`) → release.yml builds + pushes + signs all 4.
- [ ] **Step 2:** Verify all 4 images present + signed (`crane ls`, `cosign verify`).
- [ ] **Step 3:** `git mv` this plan to `completed/`.

---

## Decisions (confirmed 2026-05-20)
1. **Versioning:** microservices version **with** the Platform tag (shared `v*`). ✅
2. **First release tag:** **`v2.4.1`** (patch over v2.4.0) — ships now. ✅

## Progress
- ✅ Task 1 — crown-jewel guard in `Directory.Build.props` (scoped to the 3 microservices).
  Positive 3/3 build + negative test (Pro.Dialer → ADR-0023 error) verified.
- ✅ Task 2 — Renderer/Mail Dockerfiles modernized (dropped stale IPcom local-feed branch →
  clean nuget.org restore). CI-sim restore verified for both.
- ✅ Task 3 — `release.yml` converted to a 4-image matrix (api/realtime/renderer/mail), each
  pushed to its own ghcr repo + cosign-signed; authorized-digests reminder gated to api. YAML valid.
- ⏳ Task 4 — set ghcr package visibility public (post first publish).
- ⏳ Task 6 — release v2.4.1 + verify.

### Note: v2.4.1 re-builds the api image too
The matrix tags all 4 at `v2.4.1`, including a fresh `api:v2.4.1` (functionally identical to
v2.4.0 — only the guard/docs changed). Its manifest digest differs from v2.4.0, so the
authorized-digests allow-list needs a new `v2.4.1` api entry (runbook 2026-05-10).
