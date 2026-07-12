---
tier: MEDIANO
owner: Harol
approver: Harol
stakeholder: Platform.Web frontend team (typed-client consumer), Platform API maintainers
decision_ref: Platform/ADR-0035
---

# Proposal: openapi-typed-client (Platform host — OpenAPI export artifact)

## Why

The csat-runner incident (Web PR#159, v3.13.1-web) shipped a hand-written `CsatResponseDto`
consumer type that drifted from the real Platform contract. The cross-repo OpenAPI typed-client
train (`openapi-typed-client`, `impact.yaml`) removes that duplicate-by-hand type surface by
having Web generate its API types from Platform's own OpenAPI document instead of transcribing
DTOs by eye. Platform already emits that document at runtime (`AddOpenApi()` /
`MapOpenApi()` in `Program.cs`, gated behind `IsDevelopment()` or
`Platform:OpenApi:Enabled`), but nothing captures it as a versioned, downloadable build artifact —
Web's codegen (the sibling `web/openapi-typed-client` child change) needs a document it can fetch
in CI without standing up a full Platform host. This host change closes that gap and authors the
ADR (Platform/ADR-0035) recording the decision.

## What Changes

- **CI export job** — a build step (in `.github/workflows/ci.yml` or a dedicated job) that
  builds/runs the Api host in the `Development`-equivalent OpenAPI-enabled mode (or an
  equivalent headless generation path) and captures `/openapi/v1.json` as a versioned,
  downloadable CI artifact. No `Microsoft.Extensions.ApiDescription.Server` build-time export
  package exists yet in the repo — this change adds the export mechanism (build-time package
  or a CI-only runtime capture step; the concrete approach is a design.md decision).
- **Fixture regeneration/verification** — `fixtures/openapi-document.v1.sample.json` (the
  golden envelope sample already seeded by `/xr:change`, with field names verbatim from the real
  `CsatResponseDto`) is regenerated/verified against the real emitted document so it stays a
  faithful golden sample for Web's downstream codegen.
- **New capability spec** (`openapi-export`) documenting the export/versioning/artifact-retrieval
  contract that Web's codegen child change consumes.
- **Author `docs/decisions/0035-openapi-typed-client-contract.md` (ADR-0035)** — the
  `decision_ref` is forward-declared by `/xr:change`; this change is what authors the ADR itself,
  recording the CI-export decision and the producer/consumer boundary with Web.
- No changes to the existing runtime `/openapi/v1.json` / `/scalar/v1` endpoints or their
  gating — this change only adds the CI-time export/artifact path around the existing runtime
  surface.
- **Out of scope — the realtime (SignalR) boundary.** The Platform repo ships four executable
  hosts (Api, Realtime, Mail, Renderer); this train covers the Api host's REST surface only.
  `Verbara.Platform.Realtime`'s hub (`/hubs/platform`) is a second Web-facing producer, but hub
  messages are not representable in the OpenAPI document (no REST paths) and its typed-client
  path (`IPlatformHubClient`) lives in Pro (`Verbara.Sdk.Pro.Push.SignalR`) — typing that
  boundary end-to-end is ADR-0020's deferred follow-up ("Typed
  `IPlatformHubClient.OnCsatResponseRecorded` Hub method", owner: Pro) and would pull Pro into
  this train for a non-REST mechanism. Web keeps its 4 hand-written hub payload interfaces
  (`src/core/realtime/platform-hub.ts`) until that follow-up runs. Mail and Renderer are
  internal producers (`X-Service-Key`-protected, consumed server-to-server by the Api host)
  with no Web-facing contract.

## Capabilities

### New Capabilities

- `openapi-export`: build/CI export of the Platform OpenAPI document as a versioned,
  downloadable artifact, plus the fixture-regeneration/verification contract that keeps
  `fixtures/openapi-document.v1.sample.json` faithful to the real emitted document.

### Modified Capabilities

(none — the runtime OpenAPI surface (`AddOpenApi`/`MapOpenApi`) has no existing OpenSpec living
spec in `openspec/specs/`; this change's capability spec covers only the net-new export/artifact
behavior.)

## Impact

- **Code:** `.github/workflows/ci.yml` (or a new dedicated workflow) for the export job;
  possibly `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` if a build-time
  `Microsoft.Extensions.ApiDescription.Server` reference is the chosen mechanism (design.md
  decision) — no runtime endpoint code changes.
- **APIs:** none new or modified — this is a CI/build artifact, not a runtime endpoint.
  `/openapi/v1.json` and `/scalar/v1` are unchanged.
- **Dependencies:** none on Pro. Downstream: Web's `web/openapi-typed-client` child change
  (buildOrder 2 per `impact.yaml`) consumes the exported document for codegen; that child change
  is out of scope here.
- **Fixtures:** `fixtures/openapi-document.v1.sample.json` regenerated/verified against the real
  emitted document.
