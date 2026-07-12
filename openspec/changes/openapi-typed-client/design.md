## Context

Verbara.Platform's Api host already emits an OpenAPI 3.0 document at runtime:
`Program.cs` registers `AddOpenApi()` (Microsoft.AspNetCore.OpenApi 10.0.5,
`Directory.Packages.props:136`) gated behind `builder.Environment.IsDevelopment() ||
Platform:OpenApi:Enabled`, and serves it at `/openapi/v1.json` plus a Scalar UI at `/scalar/v1`.
That path requires a fully running host (real or stubbed DI graph, config, connection strings) to
produce the document — it is not something CI can cheaply capture today. No
`Microsoft.Extensions.ApiDescription.Server` build-time export package is referenced anywhere in
the repo, and none of the 8 existing GitHub Actions workflows build, export, or publish the
document as an artifact.

The cross-repo `openapi-typed-client` train (`impact.yaml`, Platform buildOrder 1 / producer) needs
that document available as a versioned, downloadable CI artifact so Web's sibling child change
(`web/openapi-typed-client`, buildOrder 2) can run codegen against it without standing up a live
Platform host in Web's own CI. The motivating incident (Web PR#159, v3.13.1-web) was a hand-written
`CsatResponseDto` consumer type drifting from the real contract; the fixture
`fixtures/openapi-document.v1.sample.json` (already seeded, golden envelope sample using the real
`CsatResponseDto` field names) is the proof artifact that the export must match reality, not a
hand-maintained approximation.

## Goals / Non-Goals

**Goals:**

- Produce `/openapi/v1.json`'s content as a build-time artifact CI can generate deterministically,
  without a live database/Redis/Asterisk connection and without relying on the
  `IsDevelopment()`/`Platform:OpenApi:Enabled` runtime gate.
- Version and publish that artifact so Web's codegen job can fetch a specific Platform version's
  document (CI artifact download, keyed to the commit/tag/release the same way other cross-repo
  handoffs work).
- Regenerate/verify `fixtures/openapi-document.v1.sample.json` against the real emitted document
  so the golden sample never silently drifts from the real contract again.
- Author `docs/decisions/0035-openapi-typed-client-contract.md` (ADR-0035) recording this decision
  and the producer/consumer boundary with Web.

**Non-Goals:**

- Changing the existing runtime `/openapi/v1.json` / `/scalar/v1` endpoints, their gating, or the
  `AddOpenApi()`/`MapOpenApi()` registration — those stay exactly as they are.
- Web's codegen tooling, generated `.d.ts` types, or the `customFetch` migration — that is the
  sibling `web/openapi-typed-client` child change (buildOrder 2), out of scope here.
- Any new public HTTP surface — the export is a CI/build artifact, not a new endpoint.

## Decisions

### D1 — Build-time export via `Microsoft.Extensions.ApiDescription.Server`, not a CI-runtime host capture

Two mechanisms were considered for producing the document in CI:

1. **Build-time export (chosen):** add a `Microsoft.Extensions.ApiDescription.Server`
   `PackageReference` to `Verbara.Platform.Api.csproj` (the Microsoft-supported companion to
   `Microsoft.AspNetCore.OpenApi` already in use) with `<OpenApiGenerateDocuments>true</...>`,
   which runs the ASP.NET Core document-generation pipeline at `dotnet build`/`publish` time and
   writes `<AssemblyName>.json` to the build output — no running host, no database/Redis
   connection strings, no `Platform:OpenApi:Enabled` runtime flag needed. This is the same
   document-generation code path `AddOpenApi()`/`MapOpenApi()` uses at runtime, so there is a
   single source of truth for the schema (no second hand-maintained generator).
2. **CI-runtime host capture (rejected):** start the Api host in CI (in-memory/test-double
   dependencies, `Platform:OpenApi:Enabled=true`), curl `/openapi/v1.json`, save the response.
   Rejected: requires standing up the full DI graph (or a parallel "minimal host" harness) purely
   to serve one static file, adds a live-process step to a job that is otherwise a pure build, and
   risks masking startup-config drift as an artifact-generation failure. The `AddOpenApi()`
   registration already proves the runtime path works (Program.cs); duplicating it in CI adds
   maintenance surface without adding confidence.

Build-time export wins: it is deterministic, requires no dependency stubbing, uses a
Microsoft-first-party package already in the same family as the runtime one, and is AOT-neutral
(the export runs at build time on the SDK, not inside the AOT-published binary — `IsAotCompatible`
on `Verbara.Platform.Api` is unaffected since `Microsoft.Extensions.ApiDescription.Server` is a
build/analyzer-only package, not a runtime dependency of the published app).

### D2 — CI job placement: new step in the existing `build-and-test` job, not a new workflow

The export step is added to the existing `build-and-test` job in `.github/workflows/ci.yml`
(`dotnet build Verbara.Platform.slnx -c Release`, `ci.yml:71`) rather than a new workflow file.
Rationale: the export is a build output of the same `dotnet build` invocation already running
there (via the `OpenApiGenerateDocuments` MSBuild target), so no separate checkout/restore/build
cycle is needed — appending an `actions/upload-artifact` step after the existing build step is the
minimal change. A dedicated workflow would duplicate the build matrix for no benefit.

### D3 — Fixture regeneration is a verification task, not a generator rewrite

`fixtures/openapi-document.v1.sample.json` stays a hand-curated golden **envelope** sample (per
`impact.yaml`'s comment: "field names verbatim from the real CsatResponseDto... numeric formats
are illustrative") — it is not replaced by a full copy of the real document (which would be far
larger and churn on every unrelated endpoint change, defeating its purpose as a stable fixture).
Instead, this change adds a verification task that generates the real document (via D1's
build-time export) and diffs the fixture's `CsatResponseDto`-shaped schema fragment against the
corresponding fragment of the real document, failing loudly if they diverge. This keeps the
fixture small and stable while guaranteeing it cannot silently drift the way the original
hand-written Web DTO did.

## Risks / Trade-offs

- **[Risk]** `Microsoft.Extensions.ApiDescription.Server`'s build-time generation may resolve
  endpoint metadata slightly differently than the runtime `MapOpenApi()` path (e.g. if any
  endpoint's OpenAPI metadata is computed from runtime-only state). → **Mitigation:** the
  verification task (D3) compares the build-time-exported document's `CsatResponseDto` fragment
  against both the fixture and a runtime-captured sample during change validation, surfacing any
  divergence before this change is applied.
- **[Risk]** Adding a new `PackageReference` touches `Directory.Packages.props` / the Api
  `.csproj`, which the AOT + zero-warning gates (ADR-0022) treat as high-scrutiny. →
  **Mitigation:** `Microsoft.Extensions.ApiDescription.Server` is build/analyzer-tooling only (no
  runtime assembly shipped in the published output), so it does not affect
  `IsAotCompatible`/`JsonSerializerIsReflectionEnabledByDefault` — confirmed during tasks-phase
  implementation, not assumed here.
- **[Trade-off]** The exported artifact is versioned by CI run/commit, not by an independent
  semver of its own — Web's codegen job pins to a specific Platform artifact the same way other
  cross-repo handoffs in this ecosystem work (no new versioning scheme introduced).

## Migration Plan

Additive only — no existing runtime behavior changes, no data migration. Rollout is: land the
build-time export + CI artifact upload + fixture verification in this host change; Web's
`web/openapi-typed-client` child change (created by a later `/xr:propagate`) then points its
codegen at the published artifact. No rollback beyond reverting the CI step is needed since
nothing downstream depends on the artifact until Web's child change ships.

## Open Questions

- Exact artifact retention/versioning policy (how many historical documents CI keeps downloadable)
  is left to tasks-phase implementation using the repo's existing `actions/upload-artifact`
  conventions — no precedent elsewhere in `ci.yml` dictates a different policy.
