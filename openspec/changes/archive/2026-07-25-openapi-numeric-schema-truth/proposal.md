---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Platform.Web frontend team (typed-client consumer), Platform API maintainers
decision_ref: Platform/ADR-0036
---

# Proposal: openapi-numeric-schema-truth (Platform host — strip the spurious .NET 10 numeric string arm from the emitted OpenAPI document)

## Why

The emitted OpenAPI document types **every numeric body/response field** as a `number | string`
union (`["integer","string"]` / `["number","string"]`). Verified root cause (three independent
read-only investigations, 2026-07-24): this is a **.NET 10 `Microsoft.AspNetCore.OpenApi` +
`JsonSchemaExporter` artifact** driven by ASP.NET Core's framework-default
`JsonNumberHandling.AllowReadingFromString` — **no Verbara code produces it**
(`Program.cs:1633` registers `AddOpenApi()` bare; `NumberHandling` is set nowhere;
grep for any `IOpenApiSchemaTransformer`/int64-string converter returns zero hits). Upstream issue:
[dotnet/aspnetcore #64145](https://github.com/dotnet/aspnetcore/issues/64145).

It is an **artifact, not a precision policy**: the union is blanket across every numeric format
(census in the consumer's generated file: 207×int32, 54×double, 25×int64, 2×float), route/query
params emit clean `number`, and the pre-2026-07-19 golden fixture
(`archive/2026-07-12-openapi-typed-client/fixtures/openapi-document.v1.sample.json:37-38`) shows the
intended single-typed shape. For **responses the union is false**: the server never writes
string-typed numbers — round-trip contract tests read them with `JsonElement.GetInt32()` /
`GetValue<double>()` (which throw on a string token) and pass (`CsatResponseEndpointsTests.cs:367,396`,
`AnalyticsEndpointTests.cs:49,445,597`). There are no `decimal` money fields and no `long` string-encoded
to dodge JS 2^53 (bigserial IDs are sequential; ms durations, byte sizes, and quotas are all far below
2^53; "unlimited" is `null`, not `long.MaxValue`).

Today the document merely **tolerates** the artifact: `scripts/verify-openapi-fixture.py:20-23`
compares field **names** not JSON types to avoid re-failing, and `typed-response-schemas`
(`spec.md:19`, `design.md:82`) explicitly accepts "the .NET 10 integer/string big-number union." The
cost is downstream: the sole consumer (Platform.Web) has accreted ~30 hand-written `Number()`
coercion sites to strip a `string` arm that never arrives, and its Analytics typed-client migration
(`openapi-typed-client-analytics`) is blocked on exactly this union. Correcting the document at the
source removes the class entirely, for every current and future consumer, rather than pushing a
per-consumer workaround over a contract that lies.

## What Changes (host / Platform)

- **Add an `IOpenApiSchemaTransformer`** (new — none exists) registered at `Program.cs:1633`
  (`AddOpenApi(o => o.AddSchemaTransformer<...>())`) that, for any schema whose type is a
  numeric+`string` union, rewrites it to the single numeric type. **Document-only** — it runs over
  the built `OpenApiSchema` object model, is AOT-safe (no reflection over user types), and does
  **NOT** change runtime deserialization. Explicitly **NOT** `NumberHandling.Strict`: the serializer
  keeps `AllowReadingFromString`, so any caller currently POSTing `"42"` keeps working; the contract
  states the canonical numeric form while the runtime stays lenient.
- **Exemption list is empty** (verified: no field approaches 2^53). Record an **ADR rider**: any
  *future* field that genuinely exceeds 2^53 MUST be serialized as an explicit `string` DTO property
  (so the schema says `type: string` deliberately), never reintroducing the blanket union.
- **Regenerate the fixture/manifest verification input** in the same PR — a restoration of the
  reviewed single-typed golden shape; `verify-openapi-fixture.py` may tighten to assert single JSON
  types now that the artifact is gone.
- **Author ADR-0036** (amends ADR-0035): the numeric-schema-truth transformer + the >2^53 rider.

Full corrected numeric-typing shape is pinned in `fixtures/openapi-numeric-schema.v1.json`
(verbatim from the CI-captured `fixtures/openapi-document.corrected.json`):
`CsatAggregateDto.totalResponses` is `integer`/`int32`, `averageRating` is `number`/`double`,
`DashboardKpisDto.avgWaitMs` is `number`/`double` (the DTO field is `double`), `QueueMetricsDto.waiting`
is nullable `integer` (OpenAPI 3.1 `["null","integer"]`/`int32`) — each a **single** numeric JSON type,
no `string` arm.

## Capabilities

### Modified Capabilities

- `openapi-export` (and `typed-response-schemas`): the emitted document declares numeric schema
  fields as single JSON types. Reverses the tolerated-union posture (`typed-response-schemas`
  design.md:82) at its source; the export mechanism (CI-runtime capture, ADR-0035) is unchanged.

## Impact

- **Cross-repo — see `impact.yaml`** (this change's `openspec/changes/openapi-numeric-schema-truth/impact.yaml`).
  Scope confirmed by `/xr:change` scouts: **producer** = Verbara.Platform (this host); **consumer** =
  Verbara.Platform.Web (regenerates `openapi.d.ts`, unblocks `openapi-typed-client-analytics`, retires
  ~30 `Number()` coercions, closes the ">=3-sites coercion helper" decision as obsolete). Verbara.Sdk,
  Verbara.Sdk.Pro, verbara-website: **out of scope** (no OpenAPI-consuming mechanism).
- **buildOrder**: Platform (1) lands + CI re-exports the corrected document before Web (2) regenerates
  from it — a hard barrier despite Web being decoupled (contract dependency, not NuGet).
- **No runtime behavior change** (document-only; `AllowReadingFromString` retained). No new dependency,
  no new CI job.
- **decision_ref**: Platform/ADR-0036 (new; amends Platform/ADR-0035). Root cause dotnet/aspnetcore #64145.
