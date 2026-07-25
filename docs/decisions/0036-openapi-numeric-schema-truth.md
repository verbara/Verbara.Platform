# ADR-0036: OpenAPI numeric-schema truth — strip the spurious .NET 10 `string` arm from numeric schemas

- **Status:** Accepted
- **Date:** 2026-07-25
- **Deciders:** Maintainer
- **Amends:** ADR-0035 (OpenAPI CI-export contract — the Platform/Web typed-client boundary)
- **Related:** ADR-0022 (Native AOT + no reflection over user types), the cross-repo
  `openapi-numeric-schema-truth` train (`verbara-meta`'s `impact.yaml`;
  `web/openapi-numeric-schema-truth` child change on Verbara.Platform.Web, buildOrder 2)
- **Root cause:** [dotnet/aspnetcore #64145](https://github.com/dotnet/aspnetcore/issues/64145)

## Context

The OpenAPI document Platform emits (ADR-0035's CI-runtime capture) types **every numeric
body/response field** as a `number | string` union — `["integer","string"]` for int32/int64,
`["number","string"]` for double/float. Census of the sole consumer's generated file: 207×int32,
54×double, 25×int64, 2×float, all carrying the spurious `string` arm.

Three independent read-only investigations (2026-07-24) converged on the same root cause: this is a
**.NET 10 `Microsoft.AspNetCore.OpenApi` + `JsonSchemaExporter` artifact**, not Verbara code.
ASP.NET Core's framework-default `JsonNumberHandling.AllowReadingFromString` (which lets the
serializer *read* a quoted number on input) is reflected by `JsonSchemaExporter` into the *output*
schema of every numeric, producing the union. `Program.cs` registered `AddOpenApi()` bare;
`NumberHandling` is set nowhere; a repo-wide grep for any `IOpenApiSchemaTransformer` /
int64-string converter returned zero hits.

The union is an **artifact, not a precision policy**. It is blanket across every numeric format;
route/query params emit clean `number`; and the pre-2026-07-19 golden fixture already showed the
intended single-typed shape. For **responses the union is false** — the server never writes
string-typed numbers, and round-trip contract tests read them with `JsonElement.GetInt32()` /
`GetValue<double>()` (which throw on a string token) and pass. There are no `decimal` money fields
and no `long` string-encoded to dodge JS `2^53`: bigserial IDs are sequential, ms durations / byte
sizes / quotas are all far below `2^53`, and "unlimited" is `null`, not `long.MaxValue`. **No
Platform field approaches `2^53`.**

Before this change the document merely **tolerated** the artifact: `scripts/verify-openapi-fixture.py`
compared field *names* not JSON types, and the `typed-response-schemas` spec explicitly accepted
"the .NET 10 integer/string big-number union." The cost is downstream: the sole consumer
(Platform.Web) had accreted ~30 hand-written `Number()` coercion sites to strip a `string` arm that
never arrives, and its Analytics typed-client migration was blocked on exactly this union.

## Decision

Add an **`IOpenApiSchemaTransformer`** — `NumericSchemaTruthTransformer`
(`src/Verbara.Platform.Api/OpenApi/NumericSchemaTruthTransformer.cs`) — registered on
`AddOpenApi()` at `Program.cs`:

```csharp
builder.Services.AddOpenApi(o =>
    o.AddSchemaTransformer<Verbara.Platform.Api.OpenApi.NumericSchemaTruthTransformer>());
```

For any schema whose `type` is a **numeric+`string` union**, it rewrites the type to the single
numeric type, preserving `format`, nullability, and everything else.

### Mechanism (Microsoft.OpenApi 2.9.0 / .NET 10 API)

In Microsoft.OpenApi 2.9.0, `OpenApiSchema.Type` is a **nullable `JsonSchemaType` `[Flags]` enum**
(OpenAPI 3.1 shape), not a string or a string collection. The union is a bit-OR of flags:
`Integer=4`, `Number=8`, `String=16`, `Null=1`. So the artifact is `Integer | String` (=20) or
`Number | String` (=24); a *nullable* numeric is `Integer | String | Null` (=21), serialized as the
JSON type array `["null","integer","string"]`.

The transformer's core is a pure, statically-dispatched flag operation:

```csharp
bool hasNumeric = (flags & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0;
bool hasString  = (flags & JsonSchemaType.String) != 0;
if (hasNumeric && hasString)
    schema.Type = flags & ~JsonSchemaType.String;   // clear ONLY the string bit
```

Clearing only the `String` bit preserves the numeric flag, the `Null` (nullability) flag, and the
`Format` string. A pure `string` schema (String set, no numeric) and a pure numeric schema (no
String) are both left untouched. `AddSchemaTransformer<T>()` (`T : IOpenApiSchemaTransformer`) is
the registration overload; the transformer's `TransformAsync(OpenApiSchema, OpenApiSchemaTransformerContext, CancellationToken)`
runs once per emitted schema over the built object model.

### Document-only — explicitly NOT `NumberHandling.Strict`

This is a **document-only** correction. It rewrites the built `OpenApiSchema` model and does **not**
touch `JsonNumberHandling`. The serializer keeps `AllowReadingFromString`, so any caller currently
POSTing a quoted number (`"42"`) keeps working — no runtime deserialization behavior changes. The
contract states the canonical numeric form while the runtime stays lenient. Flipping the serializer
to `NumberHandling.Strict` was rejected: it would be a real, breaking runtime change to request
leniency, far larger in blast radius than the documentation defect being fixed.

### AOT-safe

Schema transformers run over the OpenAPI object model; there is no reflection over user types
(ADR-0022). The Api project (`<JsonSerializerIsReflectionEnabledByDefault>false</...>`,
`<IsAotCompatible>true</...>`) builds clean with the transformer registered.

### Blanket over every numeric format, EMPTY exemption list — the `2^53` rider

The transformer applies uniformly to int32/int64/double/float. The exemption list is **empty**
(verified: no field exceeds `2^53`). **Rider:** any *future* field that genuinely exceeds `2^53`
(where JS `number` loses integer precision) MUST be serialized as an **explicit `string` DTO
property** — so the schema declares `type: string` *deliberately* and the client reads it as a
string — never by reintroducing the blanket numeric union. This keeps "this field is a string on the
wire because precision demands it" (a deliberate design choice, one field) distinct from "every
number is nominally also a string" (the artifact, blanket).

### Verification tightened

`scripts/verify-openapi-fixture.py` now, in addition to its per-group field-name check, scans the
whole captured document and **fails** on any surviving numeric+`string` union (nullable numerics
`["null","integer"]` / `["null","number"]` are legitimate and pass). The tolerance ADR-0035 carried
("JSON types intentionally NOT compared") is upgraded to an enforced invariant now that the artifact
is gone at the source. The corrected document (`fixtures/openapi-document.corrected.json`, captured
in-memory via the `Platform:OpenApi:Enabled=true` `WebApplicationFactory` path) passes with 0 unions
across 391 schemas; a `NumericSchemaTruthCaptureTests` integration test asserts the same
whitespace-insensitively on every run.

## Consequences

- **Positive:** the emitted contract stops lying — every numeric declares a single JSON type. Web's
  codegen (buildOrder 2) regenerates `openapi.d.ts` into clean single-typed numbers, unblocking the
  held `openapi-typed-client-analytics` change and retiring ~30 `Number()` coercion sites for every
  current and future consumer, rather than pushing a per-consumer workaround over a contract that lies.
- **Positive:** the numeric-truth invariant is now CI-guarded (`verify-openapi-fixture.py`), so a
  transformer regression or a hand-added union fails loudly instead of silently re-accreting downstream
  coercions.
- **Neutral / no runtime change:** document-only; `AllowReadingFromString` retained. No new
  dependency, no new CI job — the transformer registers on the existing `AddOpenApi()` surface and the
  capture reuses ADR-0035's CI-runtime export.
- **Neutral:** the golden fixture `fixtures/openapi-numeric-schema.v1.json` was corrected to match the
  real captured document (`DashboardKpisDto.avgWaitMs`/`avgHandleTimeMs` are `double`, not the
  hand-authored seed's `int64`; nullable numerics render as OpenAPI-3.1 `["null","integer"]`, not a
  `nullable: true` keyword) — the captured document is authoritative.

## Alternatives considered

- **`NumberHandling.Strict` on the serializer:** rejected — a breaking runtime change to request
  leniency (callers POSTing `"42"` would start failing), disproportionate to fixing a documentation
  defect. The transformer corrects the *document* without touching *runtime*.
- **A per-consumer coercion helper in Web** (the deferred ">=3-genuine-sites shared coercion helper"
  decision): rejected as obsolete — it treats the symptom in one consumer over a contract that lies,
  where this change removes the class at the source for all consumers. That Web decision is closed as
  OBSOLETE by this change.
- **A non-empty exemption list / opt-in allowlist of "safe" numerics:** rejected — no field is unsafe
  (all below `2^53`), so an allowlist would be empty machinery. The `2^53` rider governs the only future
  case (explicit `string` DTO property), keeping the exemption list permanently empty.
- **Leaving the union and documenting it** (the pre-change posture): rejected — the union is false for
  responses and the downstream coercion cost compounds per consumer and per migration.
