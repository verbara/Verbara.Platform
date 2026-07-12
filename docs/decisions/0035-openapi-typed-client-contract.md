# ADR-0035: OpenAPI CI-export contract — the Platform/Web typed-client boundary

- **Status:** Accepted
- **Date:** 2026-07-12
- **Deciders:** Maintainer
- **Related:** ADR-0022 (Native AOT + no Dapper shipping path), ADR-0020 (CSAT runner, the DTO
  this change's fixture was seeded from), the cross-repo `openapi-typed-client` train
  (`verbara-meta`'s `impact.yaml`, `web/openapi-typed-client` child change on
  Verbara.Platform.Web, buildOrder 2)

## Context

The csat-runner incident (Web PR#159, v3.13.1-web) shipped a hand-written `CsatResponseDto`
consumer type in Platform.Web that drifted from the real Platform contract. The cross-repo
`openapi-typed-client` train removes that duplicate-by-hand REST type surface by having Web
generate its API types from Platform's own OpenAPI document instead of transcribing DTOs by eye.

Platform's Api host already emits an OpenAPI 3.0 document at runtime: `Program.cs` registers
`AddOpenApi()` (`Microsoft.AspNetCore.OpenApi` 10.0.5) gated behind
`builder.Environment.IsDevelopment() || Platform:OpenApi:Enabled`, serving it at
`/openapi/v1.json` plus a Scalar UI at `/scalar/v1`. Producing that document requires a fully
running host — nothing in this repo's 8 GitHub Actions workflows captured it as a versioned,
downloadable CI artifact, so Web's codegen (the sibling `web/openapi-typed-client` change) had no
document to fetch in its own CI without standing up a full Platform host.

This ADR records the CI-export decision, the producer/consumer boundary with Web, and — because
implementation diverged from the original proposal — the pivot from a build-time to a
CI-runtime export mechanism, with the experimental evidence for why.

## Decision

Platform's Api host OpenAPI document is captured for cross-repo consumption via **CI-runtime host
capture**: the `build-and-test` job in `.github/workflows/ci.yml` starts the already-built
`Verbara.Platform.Api.dll` against an ephemeral, job-scoped Postgres `services:` container with
`Platform:OpenApi:Enabled=true`, polls `/openapi/v1.json` until it responds, saves the response as
`artifacts/openapi/openapi-document.json`, stops the host, verifies the document's
`CsatResponseDto` schema fragment against the golden fixture
(`openspec/changes/archive/2026-07-12-openapi-typed-client/fixtures/openapi-document.v1.sample.json`,
via `scripts/verify-openapi-fixture.py`), and uploads the document as a versioned, run-scoped CI
artifact (`openapi-document-${{ github.sha }}`, `actions/upload-artifact`). Web's
`web/openapi-typed-client` child change (buildOrder 2) downloads that artifact for its codegen.

No change to the existing runtime `/openapi/v1.json` / `/scalar/v1` endpoints or their
`IsDevelopment() || Platform:OpenApi:Enabled` gating — this is purely a CI-time artifact
capture around the existing runtime surface.

### Deviation from the original proposal: build-time export tried and rejected

The change was proposed with **build-time export** as D1 (see the change's `design.md`): add a
`Microsoft.Extensions.ApiDescription.Server` `PackageReference` to
`Verbara.Platform.Api.csproj`, which runs the ASP.NET Core document-generation pipeline at
`dotnet build` time via the `dotnet-getdocument` out-of-process tool, with no running host and no
database connection needed. This was rejected as unworkable during implementation, not assumed at
propose-time. Evidence:

1. `dotnet-getdocument`'s `HostFactoryResolver` does not stop at `builder.Build()` — it calls
   `IHost.StartAsync()` on the real, fully-constructed `WebApplicationBuilder`-built host, which
   starts every registered `IHostedService` (this Program.cs registers ~28) before ever reaching
   `MapOpenApi()`'s document-serving code.
2. With zero connection strings configured (the entire point of "build-time, no live DB" export),
   this throws `Program.cs`'s own hard guard at the DataProtection registration:
   `InvalidOperationException: ConnectionStrings:Postgres is required for DataProtection key
   persistence (ADR-0003) in non-Testing environments` — unless `ASPNETCORE_ENVIRONMENT=Testing`.
3. Past that (with `Environment=Testing`), a *different* failure surfaces:
   `RealtimeStateBridge` (an `IHostedService`) eagerly resolves `IRealtimeSyncService` during
   `AddVerbaraRealtime()`'s container build, which in turn resolves `EndpointProfileStoreBase` — a
   type only registered by `UsePostgresRealtimeStorage`, itself only called when a
   `Realtime`/`Analytics`/`Postgres` connection string is configured. Result:
   `InvalidOperationException: Unable to resolve service for type 'EndpointProfileStoreBase'`.
4. Supplying a fake/unreachable connection string to route past step 3 does not help either — it
   moves the failure to a genuine (failing) `Npgsql` connection attempt inside
   `SchemaMigrator.EnsureSchemaAsync`, called eagerly at service-registration time by
   `UsePostgresRealtimeStorage`.

There is no configuration-only escape from this: **this host cannot reach a request-serving state
without a genuinely reachable Postgres**, independent of the OpenAPI feature entirely. This is a
pre-existing property of `Program.cs`'s Pro-module wiring (several `Verbara.Sdk.Pro.*` packages'
default DI registrations assume a reachable database during hosted-service startup), not something
introduced by this change, and fixing it — changing `Verbara.Sdk.Pro.Realtime`'s registration
defaults to tolerate a no-Postgres deployment — is out of scope for an Api-host-only change (the
existing test suite avoids this entirely differently: `WebApplicationFactory`-based fixtures like
`UnifiedPlatformApiFactory` surgically remove every `Asterisk`/`Verbara`-implemented
`IHostedService` and substitute mocks, machinery that exists only inside test infrastructure, not
in `Program.cs`).

Since build-time export cannot work at all for this Program.cs without changes to Pro's DI
registration defaults, the remaining option — CI-runtime host capture — was adopted, with a real
(if ephemeral, CI-only) Postgres backing it rather than the "in-memory/test-double dependencies"
originally assumed and rejected at propose-time. The original rejection rationale for CI-runtime
capture ("adds a live-process step... duplicating maintenance surface without adding confidence")
no longer distinguishes the two options once build-time export is shown to pay the exact same
DI-graph-standup cost via a different entry point (`dotnet-getdocument` instead of `dotnet run`).

`Microsoft.Extensions.ApiDescription.Server` was NOT added to this repo as a result of the pivot —
`Directory.Packages.props` and `Verbara.Platform.Api.csproj` are unchanged from `main` (the
package reference was added and then reverted once the build-time attempt proved infeasible).

### A second, unrelated pre-existing bug found and fixed during validation

Validating the CI-runtime capture mechanism required actually invoking `/openapi/v1.json` for
what appears to be the first time in this environment. It returned HTTP 500:
`System.NotSupportedException: JsonTypeInfo metadata for type
'System.Nullable\`1[Verbara.Platform.Conversations.ConversationState]' was not provided by
TypeInfoResolver`. Root cause: `ConversationEndpoints.ListConversations`'s `ConversationState?
state` handler parameter (and, once that was fixed, `AuditEndpoints.SearchAuditLog`'s `Guid?
correlationId`) are bare nullable value-typed route-handler parameters that were previously
reachable in `ApiJsonContext` only as **nested** members of other registered DTOs, never
registered as **root** `[JsonSerializable]` types — a gap `Microsoft.AspNetCore.OpenApi`'s schema
generator (`JsonSchemaExporter`) requires filled for any type used as a parameter's own schema
(ADR-0022 Phase C already flagged this exact class of gap: "if a new enum type is added without a
context entry, serialization will throw at call-time"). Fixed by adding
`[JsonSerializable(typeof(ConversationState?))]` and `[JsonSerializable(typeof(Guid?))]` to
`ApiJsonContext.cs` — additive schema-metadata-only entries; a full sweep of every bare
nullable-enum/struct handler parameter across `src/Verbara.Platform.Api/Endpoints/**` found no
further instances of the pattern. This fix does not change any endpoint's request/response
contract, gating, or status codes — only `/openapi/v1.json`'s own document generation, previously
throwing, now succeeds.

### Verification

The fixture (`fixtures/openapi-document.v1.sample.json`) was compared against the real
CI-runtime-captured document's `CsatResponseDto` schema fragment (via
`scripts/verify-openapi-fixture.py`, run against a locally-captured document during
implementation as a stand-in for the CI step). Result: all 6 required fields (`queueName`,
`channel`, `totalResponses`, `averageRating`, `rangeStart`, `rangeEnd`) match by name and type
family; **no fixture update was needed**. One documented, non-blocking difference: the real
document's path key is `/api/v{version}/analytics/csat/queues/{queueId}` (Asp.Versioning.Http
renders the version segment as a template placeholder, not the resolved literal `v1`), vs. the
fixture's `/api/v1/...` — an envelope/path-key difference, not a `CsatResponseDto` field
difference, and the verification script compares the schema fragment directly rather than
requiring an exact path-string match.

## Consequences

- Positive: Web's codegen (buildOrder 2) can fetch a real, versioned Platform OpenAPI document
  from CI without standing up a live Platform host in its own pipeline.
- Positive: fixed a genuine, previously-undetected `/openapi/v1.json` 500 (the two
  `ApiJsonContext` root-type gaps) — this document was, in effect, non-functional in this
  environment before this change.
- Negative / trade-off: the export step depends on a real (if ephemeral) Postgres service
  container and a background host process rather than a pure `dotnet build` output, adding
  roughly 10-20 seconds to the `build-and-test` job (container start + host boot + poll).
- Neutral: the exported artifact is versioned by CI run/commit (`openapi-document-${{
  github.sha }}`), not an independent semver — matching how other cross-repo handoffs in this
  ecosystem already work; no new versioning scheme introduced.
- Neutral: `Verbara.Platform.Realtime`'s SignalR hub (`/hubs/platform`) remains explicitly out of
  scope — hub messages aren't representable in an OpenAPI document, and typing that boundary is
  ADR-0020's deferred follow-up (owner: Pro).

## Alternatives considered

- **Build-time export via `Microsoft.Extensions.ApiDescription.Server`:** the original proposal.
  Rejected during implementation — see the Deviation section above for the full experimental
  trace; not viable for this Program.cs without invasive changes to Pro package DI defaults.
- **Fixing Pro's `Verbara.Sdk.Pro.Realtime` DI registrations to tolerate zero Postgres connection
  strings** (which would have unblocked build-time export): rejected as out of scope for an
  Api-host-only change — it touches closed-source Pro package internals and default behavior for
  every consumer of `AddVerbaraRealtime()`, not just the OpenAPI-export use case.
- **A new, dedicated GitHub Actions workflow for the export** (instead of a step in the existing
  `build-and-test` job): rejected — would duplicate the checkout/restore/build cycle for no
  benefit; the export step reuses the job's already-built `Verbara.Platform.Api.dll`.
