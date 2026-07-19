# openapi-export Specification

## Purpose
Captures the Api host's OpenAPI 3.0 document as a versioned, downloadable CI artifact so
downstream consumers — chiefly Platform.Web's typed-client codegen (`web/openapi-typed-client`,
buildOrder 2 in the cross-repo `openapi-typed-client` train) — can generate API types from the
real contract instead of hand-transcribing DTOs (the failure mode behind the csat-runner
incident, Web PR#159, v3.13.1-web). See `docs/decisions/0035-openapi-typed-client-contract.md`
for the CI-runtime-capture decision and the build-time-export pivot it records.
## Requirements
### Requirement: CI exports the OpenAPI document via runtime capture

> **DEVIATION (recorded during tasks-phase implementation):** this requirement originally
> specified a build-time mechanism (`Microsoft.Extensions.ApiDescription.Server` document
> generation without a running host). Implementation found that mechanism infeasible for this
> repo's `Verbara.Platform.Api` host — its `dotnet-getdocument` tool starts every registered
> `IHostedService`, several of which (Pro Realtime/Cluster/EventStore modules) require a live,
> reachable Postgres connection to complete DI registration/startup for reasons unrelated to
> OpenAPI, with no configuration-only escape (see `design.md` D1's deviation note for the full
> experimental trace). This requirement is updated to CI-runtime host capture: the same
> `AddOpenApi()`/`MapOpenApi()` runtime code path, exercised via one bounded HTTP round-trip
> against a briefly-running host backed by an ephemeral, CI-only Postgres, rather than an
> in-process build-time call. The document produced is byte-identical in content terms (same
> `OpenApiDocumentService`), so the "single source of truth for the schema" property the
> build-time mechanism was chosen for is preserved.

The build SHALL produce the Api host's OpenAPI 3.0 document as a CI artifact by starting the
built `Verbara.Platform.Api` binary with `Platform:OpenApi:Enabled=true` against an ephemeral,
CI-only Postgres connection (a GitHub Actions `services:` container, not a persistent or shared
database), retrieving `/openapi/v1.json` over HTTP once the host reports ready, and then stopping
the host — without any manual runtime deployment, without exercising a live production/shared
database, and independent of `ASPNETCORE_ENVIRONMENT`'s default value (the step sets both
`Platform:OpenApi:Enabled` and the ephemeral connection string explicitly rather than relying on
`IsDevelopment()`).

#### Scenario: A CI run produces the document via a briefly-running host

- **GIVEN** a completed `dotnet build Verbara.Platform.slnx -c Release` in CI, with an ephemeral
  Postgres service container available to the job
- **WHEN** the export step starts `Verbara.Platform.Api` in the background with
  `Platform:OpenApi:Enabled=true` and the ephemeral Postgres connection string, and polls
  `/openapi/v1.json` until it responds
- **THEN** the generated OpenAPI document for `Verbara.Platform.Api` is retrieved and saved,
  independent of the ambient `ASPNETCORE_ENVIRONMENT` value, and the host is stopped afterward

### Requirement: CI publishes the exported document as a versioned, downloadable artifact

The `build-and-test` workflow job SHALL upload the exported OpenAPI document as a CI artifact
scoped to the triggering commit/run, so downstream consumers (Web's typed-client codegen) can
download a specific Platform version's document without checking out or building Platform.

#### Scenario: Artifact is retrievable after a CI run

- **GIVEN** a completed `build-and-test` job on a commit
- **WHEN** a consumer requests that run's artifacts
- **THEN** the OpenAPI document produced from that commit is downloadable as a named CI artifact

### Requirement: The golden fixture is verified against the real emitted document

`fixtures/openapi-document.v1.sample.json` SHALL be checked, as part of this change's validation,
against the corresponding schema fragment of the real CI-runtime-captured OpenAPI document, so the
fixture cannot silently drift from the real contract the way the hand-written Web `CsatResponseDto`
consumer type did (Web PR#159, v3.13.1-web).

The exported document SHALL match the golden wire fixture verbatim (the
verbatim-fixture-citation rule, `/xr:propagate`): the envelope keys `openapi`, `info`, `paths`,
and `components.schemas`; the sample path (with the version-template caveat noted below); and
the `CsatResponseDto` schema's 6 fields exactly:

- `queueName`
- `channel`
- `totalResponses`
- `averageRating`
- `rangeStart`
- `rangeEnd`

> **Implementation note (path template):** the real document's path key for this endpoint is
> `/api/v{version}/analytics/csat/queues/{queueId}` (Asp.Versioning.Http's URL-segment versioning
> renders the version segment as a template placeholder in the OpenAPI document, not the resolved
> literal `v1`), whereas the fixture uses `/api/v1/analytics/csat/queues/{queueId}`. This is a
> path-key/envelope difference, not a `CsatResponseDto` field-name/type difference — the
> verification task compares the `CsatResponseDto` schema fragment directly (via
> `components.schemas.CsatResponseDto`), not by requiring an exact path-string match, so this does
> not block verification. Documented here since it is a real, previously-unrecorded emitted-
> document detail discovered while validating this change.

#### Scenario: Fixture matches the real document's schema fragment

- **GIVEN** the CI-runtime-captured OpenAPI document and the existing
  `fixtures/openapi-document.v1.sample.json`
- **WHEN** the fixture's `CsatResponseDto` schema fragment is compared against the corresponding
  fragment in the real document (via `components.schemas.CsatResponseDto`)
- **THEN** field names and types match exactly (numeric/date-time formats in the fixture remain
  illustrative per the fixture's own documented intent)

### Requirement: The runtime OpenAPI surface is unaffected

Adding the CI export mechanism SHALL NOT change the existing runtime behavior of
`/openapi/v1.json` or `/scalar/v1`, including their `IsDevelopment() ||
Platform:OpenApi:Enabled` gating.

> **Implementation note:** validating this requirement (tasks-phase) required actually invoking
> `/openapi/v1.json` for the first time in this environment, which surfaced two pre-existing
> HTTP 500s in that endpoint's OWN document-generation logic (see `design.md` Risks — bare
> `ConversationState?`/`Guid?` handler parameters missing root `[JsonSerializable]` entries). Fixing
> those was necessary for `/openapi/v1.json` to return 200 at all; per the analysis in `design.md`,
> the fix is additive schema metadata only and changes no endpoint's request/response contract,
> gating, or status codes — the requirement below (gating unaffected) still holds and was verified
> against the fixed build.

#### Scenario: Runtime endpoints remain gated as before

- **GIVEN** a Production deployment with `Platform:OpenApi:Enabled` unset
- **WHEN** a client requests `/openapi/v1.json` or `/scalar/v1`
- **THEN** the endpoints remain unavailable, exactly as before this change

### Requirement: The completed response-schema manifest is verified against the real emitted document

`fixtures/response-schema-manifest.v1.json` SHALL, once completed by this change, be asserted against
the CI-runtime-captured OpenAPI document as part of validation: for every group
(`csat`, `admin-remainder`, `agent`, `analytics`, `operations`) and every schema the group declares,
the schema MUST exist in the captured document's `components/schemas` under its declared EMITTED name,
and its property names MUST match the manifest's declared field names verbatim. This extends the
existing single-`CsatResponseDto`-fragment check (`scripts/verify-openapi-fixture.py`) to a
per-group, per-schema, verbatim-field-name assertion driven by the manifest.

The manifest MUST record EMITTED schema names — which can differ from the C# record name (the
`CsatAggregateDto` C# record emits as `CsatResponseDto`, the naming lesson captured in the manifest's
`$comment`). Numeric/date-time formats remain illustrative and are NOT compared, consistent with the
existing fragment check's documented intent (type families — string vs number vs integer, ignoring
the .NET 10 integer/string big-number union — are still compared).

Manifest completeness is a precondition of the cross-repo fan-out (`/xr:propagate`): a group left in
`TO-COMPLETE-BY-HOST` state at propagate time is a blocking fixture-completeness finding (T11,
verbatim-fixture-citation rule).

#### Scenario: A completed manifest group is verified against the captured document

- **GIVEN** the CI-runtime-captured OpenAPI document and a completed
  `response-schema-manifest.v1.json` group (e.g. `admin-remainder`)
- **WHEN** `verify-openapi-fixture.py` asserts the manifest against the captured document
- **THEN** each declared schema in the group is found in `components/schemas` under its declared
  emitted name with matching property names, and the check passes; a missing schema or a field-name
  mismatch fails the check

#### Scenario: An incomplete manifest group blocks propagation

- **GIVEN** the manifest with any group still marked `TO-COMPLETE-BY-HOST`
- **WHEN** the change is evaluated for cross-repo fan-out
- **THEN** the incomplete group is reported as a blocking fixture-completeness finding and
  `/xr:propagate` does not proceed

