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

### D1 — CI-runtime host capture (build-time export tried and rejected during implementation)

> **DEVIATION (recorded during tasks-phase implementation, not assumed at propose-time):** this
> section originally chose build-time export via `Microsoft.Extensions.ApiDescription.Server` and
> rejected CI-runtime host capture. Implementation proved the build-time path infeasible for this
> specific Program.cs and pivoted to CI-runtime host capture instead. Both alternatives, and why
> the pivot happened, are recorded below per the apply-time deviation protocol (report to
> orchestrator + update design.md/spec.md in-change; `impact.yaml` and the fixture are untouched).

Two mechanisms were considered for producing the document in CI:

1. **Build-time export (tried, then rejected):** add a `Microsoft.Extensions.ApiDescription.Server`
   `PackageReference` to `Verbara.Platform.Api.csproj` (the Microsoft-supported companion to
   `Microsoft.AspNetCore.OpenApi` already in use) with `<OpenApiGenerateDocuments>true</...>`,
   which runs the ASP.NET Core document-generation pipeline at `dotnet build`/`publish` time via
   the `dotnet-getdocument` tool's `HostFactoryResolver`. **Implementation finding:** this tool
   does not stop at `builder.Build()` — it calls `IHost.StartAsync()` on the real
   `WebApplicationBuilder`-built host, which starts all ~28 `IHostedService`s registered in
   `Program.cs` before ever reaching `MapOpenApi()`'s document-serving code. With zero connection
   strings configured (the whole point of build-time export — no live DB), this hits, in order:
   (a) `Program.cs`'s own hard `throw` at the DataProtection registration
   ("`ConnectionStrings:Postgres` is required ... in non-Testing environments", `Program.cs:~705`)
   unless `ASPNETCORE_ENVIRONMENT=Testing`; (b) once past that, `RealtimeStateBridge` (an
   `IHostedService`) eagerly resolves `IRealtimeSyncService` during `AddVerbaraRealtime()`'s
   container build, which resolves `EndpointProfileStoreBase` — a type only registered by
   `UsePostgresRealtimeStorage`, itself only called when a `Realtime`/`Analytics`/`Postgres`
   connection string is configured — producing
   `InvalidOperationException: Unable to resolve service for type 'EndpointProfileStoreBase'`.
   Supplying a fake/unreachable connection string to route past (b) just moves the failure to a
   real (failing) `Npgsql` connection attempt inside `SchemaMigrator.EnsureSchemaAsync`, called
   eagerly from `UsePostgresRealtimeStorage` at service-registration time. There is no
   configuration-only escape: **this host cannot reach a request-serving state without a genuinely
   reachable Postgres**, a pre-existing property of `Program.cs`'s Pro-module wiring, unrelated to
   OpenAPI and out of scope to fix here (it would mean changing `Verbara.Sdk.Pro.Realtime`'s
   default DI registrations, Pro package surface, for an Api-host-only change). Confirmed
   experimentally: `dotnet build`/`dotnet run` with `ASPNETCORE_ENVIRONMENT=Testing` and no
   connection strings throws exactly this `EndpointProfileStoreBase` resolution failure; the
   existing test suite only avoids it because `WebApplicationFactory`-based test fixtures
   (`UnifiedPlatformApiFactory` et al.) surgically remove every `Asterisk`/`Verbara`-implemented
   `IHostedService` and swap in mocks — machinery that exists only inside test infrastructure, not
   in `Program.cs` itself, and reproducing it there is out of scope.
2. **CI-runtime host capture (chosen after the pivot):** start the Api host for real, with a
   genuinely reachable (but ephemeral, CI-only) Postgres backing it and
   `Platform:OpenApi:Enabled=true`, curl `/openapi/v1.json`, save the response, then stop the
   host. Originally rejected on the assumption that only "in-memory/test-double dependencies"
   were available and that standing up a full DI graph for one static file wasn't worth a
   live-process CI step. The build-time attempt above proved the DI-graph-standup cost is
   unavoidable **either way** — build-time export pays it too, just via a different entry point
   (`dotnet-getdocument`'s `HostFactoryResolver` instead of `dotnet run`) — so the original
   rejection rationale ("adds a live-process step... duplicating maintenance surface") no longer
   distinguishes the two options. Given build-time export is outright broken for this Program.cs,
   CI-runtime host capture is the only mechanism of the two that actually produces a document.
   **Experimentally validated** (tasks-phase, this change): with an ephemeral `postgres:18-alpine`
   backing `ConnectionStrings:Postgres` and `Platform:OpenApi:Enabled=true`, the host reaches
   "Application started" and `GET /openapi/v1.json` returns HTTP 200 with a
   182-schema/324-path document containing the expected `CsatResponseDto` fragment (see the
   Risks section below for two runtime bugs this validation run also surfaced and fixed).

CI-runtime host capture wins **not** because it was originally preferred, but because build-time
export cannot work at all for this Program.cs without invasive changes to Pro's DI registration
defaults, which is out of scope for an Api-host-only change. The runtime document-generation code
path (`AddOpenApi()`/`MapOpenApi()`) is still the single source of truth for the schema either
way — CI-runtime capture calls the exact same code, just via an HTTP round-trip instead of an
in-process build-time call.

### D2 — CI job placement: new steps in the existing `build-and-test` job, using a Postgres service container, not a new workflow

> **DEVIATION follow-on:** D1's pivot to CI-runtime host capture means the export step needs a
> reachable Postgres, which the original build-time plan didn't. This is accommodated with a
> GitHub Actions `services:` Postgres container scoped to `build-and-test` — not a new job/workflow
> and not the heavier Testcontainers-in-.NET-code pattern `live-db-tests` uses (that job's
> containers are managed from inside the test process for per-fixture isolation; this export step
> needs exactly one long-lived DB for one CI run, which `services:` provides declaratively with
   no extra C# harness).

The export step is added to the existing `build-and-test` job in `.github/workflows/ci.yml`
(after `dotnet build Verbara.Platform.slnx -c Release`, `ci.yml:71`) rather than a new workflow
file, preserving the original D2 rationale that motivated staying in one job: no separate
checkout/restore/build cycle, minimal diff. What changed is *what* the step does — instead of
reading a build-output file, it (1) relies on a `services: postgres:` container GitHub Actions
starts alongside the job, (2) launches the already-built `Verbara.Platform.Api.dll` in the
background with `ASPNETCORE_ENVIRONMENT=Development`, `Platform__OpenApi__Enabled=true`, and
`ConnectionStrings__Postgres` pointing at the service container, (3) polls
`/openapi/v1.json` until it responds 200 (bounded retry, not a fixed sleep), (4) saves the
response body as the artifact, (5) stops the host. A dedicated workflow would still duplicate the
build matrix for no benefit — that part of the original rationale is unchanged by the pivot.

### D3 — Fixture regeneration is a verification task, not a generator rewrite

`fixtures/openapi-document.v1.sample.json` stays a hand-curated golden **envelope** sample (per
`impact.yaml`'s comment: "field names verbatim from the real CsatResponseDto... numeric formats
are illustrative") — it is not replaced by a full copy of the real document (which would be far
larger and churn on every unrelated endpoint change, defeating its purpose as a stable fixture).
Instead, this change adds a verification task that generates the real document (via D1's
CI-runtime capture, post-pivot) and diffs the fixture's `CsatResponseDto`-shaped schema fragment
against the corresponding fragment of the real document, failing loudly if they diverge. This
keeps the fixture small and stable while guaranteeing it cannot silently drift the way the
original hand-written Web DTO did.

## Risks / Trade-offs

- **[Risk, materialized]** Build-time export via `Microsoft.Extensions.ApiDescription.Server` was
  the original D1 choice; implementation found it infeasible (see D1's deviation note) because the
  `dotnet-getdocument` tool starts the real host's `IHostedService`s, which require a live Postgres
  for reasons unrelated to OpenAPI. → **Resolution:** pivoted to CI-runtime host capture (D1) with
  a real ephemeral Postgres service container (D2); `Microsoft.Extensions.ApiDescription.Server`
  is NOT added to this repo as a result — `Directory.Packages.props` and
  `Verbara.Platform.Api.csproj` are unchanged by this deviation (reverted after the initial attempt
  proved infeasible).
- **[Risk, materialized]** The CI-runtime capture validation run (tasks-phase) found
  `/openapi/v1.json` genuinely returning HTTP 500 in this environment today — a pre-existing,
  unrelated-to-this-change gap: `ConversationEndpoints.ListConversations`' `ConversationState?
  state` and `AuditEndpoints.SearchAuditLog`'s `Guid? correlationId` handler parameters are bare
  nullable value types never registered as ROOT `[JsonSerializable]` entries in `ApiJsonContext`
  (only reachable as nested members of other registered DTOs before this change), and
  `Microsoft.AspNetCore.OpenApi`'s schema generator (`JsonSchemaExporter`) requires root metadata
  for any type used as a request parameter's own schema. → **Resolution:** added
  `[JsonSerializable(typeof(ConversationState?))]` and `[JsonSerializable(typeof(Guid?))]` to
  `ApiJsonContext.cs` — additive schema-metadata-only entries, confirmed to add no wire-format or
  endpoint-behavior change (verified: request/response shapes, gating, and status codes for both
  endpoints are unchanged; only `/openapi/v1.json`'s own document generation, which was previously
  throwing, now succeeds). A full sweep of every bare nullable-enum/struct handler parameter across
  `Endpoints/**` (Task-phase research) found no further instances of this pattern.
- **[Risk]** CI-runtime generation may resolve endpoint metadata slightly differently than a
  hypothetical build-time path would have (e.g. runtime-only state feeding into OpenAPI metadata).
  → **Mitigation:** the verification task (D3) compares the CI-runtime-captured document's
  `CsatResponseDto` fragment against the fixture during change validation; no divergence beyond the
  fixture's own documented illustrative-format allowance was found.
- **[Trade-off]** The exported artifact is versioned by CI run/commit, not by an independent
  semver of its own — Web's codegen job pins to a specific Platform artifact the same way other
  cross-repo handoffs in this ecosystem work (no new versioning scheme introduced).
- **[Trade-off, new]** The export step now depends on a real (if ephemeral, CI-only) Postgres
  service container and a background host process, rather than a pure `dotnet build` output. This
  adds ~10-20s to `build-and-test` (container start + host boot + poll) but was unavoidable once
  D1's build-time path proved infeasible — the alternative (invasive Pro DI-registration changes to
  make the host startable with zero connection strings) was explicitly out of scope for an
  Api-host-only change.

## Migration Plan

Additive only — no existing runtime endpoint BEHAVIOR changes (the two `ApiJsonContext` additions
fix a pre-existing 500 in document generation itself, not any endpoint's request/response
contract), no data migration. Rollout is: land the CI-runtime export + CI artifact upload + fixture
verification + the two `ApiJsonContext` root-type fixes in this host change; Web's
`web/openapi-typed-client` child change (created by a later `/xr:propagate`) then points its
codegen at the published artifact. No rollback beyond reverting the CI step is needed since
nothing downstream depends on the artifact until Web's child change ships.

## Open Questions

- Exact artifact retention/versioning policy (how many historical documents CI keeps downloadable)
  is left to tasks-phase implementation using the repo's existing `actions/upload-artifact`
  conventions — no precedent elsewhere in `ci.yml` dictates a different policy.
