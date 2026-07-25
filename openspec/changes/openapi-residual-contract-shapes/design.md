## Context

The archived `openapi-numeric-schema-truth` change (Platform/ADR-0036) introduced the
`IOpenApiSchemaTransformer` seam — `src/Verbara.Platform.Api/OpenApi/NumericSchemaTruthTransformer.cs`,
registered at `Program.cs:1633` via `builder.Services.AddOpenApi(o => o.AddSchemaTransformer<…>())` —
that rewrites the built `OpenApiSchema` object model **document-only** so the emitted OpenAPI document
tells the truth without touching runtime serialization. That change closed the blanket
`number | string` union but left three residual contract-shape divergences that Platform.Web's
`[Unreleased]` changelog logged as three separate Platform contract bugs.

Read-only `/xr:change` scouts (Platform + Platform.Web, 2026-07-25) reconciled all three against
source. The finding, pinned in the three golden fixtures in this change's `fixtures/`:

1. **`ComplianceRuleSummaryDto.severity` is the one genuine producer divergence.** The DTO
   (`Endpoints/CallAnalyticsEndpoints.cs:376`) types `Severity` as a plain `string`, so the emitted
   schema advertises an open string where the intended contract is the closed literal union
   `Info | Warning | Critical`. The three values are not invented here — the sibling
   `ComplianceSeverityBreakdownDto` (`CallAnalyticsEndpoints.cs:382-385`) has exactly three integer
   members `Info`, `Warning`, `Critical`, enumerating the severity domain.
2. **`TopicTrends` `topics`→`trends` is a Web-only shadow bug.** Platform source is already
   `record TopicTrendsResponse(TopicTrendDto[] Trends, int TotalAnalyzed)`
   (`CallAnalyticsEndpoints.cs:355`), which serializes `trends`. The stale `topics` exists only in
   Web's hand-written shadow — no host divergence.
3. **`PagedResult` envelope already agrees.** `Platform.Core/PagedResult.cs` and Web's generated
   envelope match field-for-field; the only "divergence" is `openapi-typescript`'s `PagedResultOf<T>`
   monomorphization, inherent to how the codegen expands a generic — not a schema defect.

Hard constraints (Platform/ADR-0022): Native AOT, no reflection over user types, every (de)serialized
DTO in a `[JsonSerializable]` source-gen context (`Serialization/ApiJsonContext.cs`),
`TreatWarningsAsErrors=true`.

## Goals / Non-Goals

**Goals:**

- Make the emitted OpenAPI document declare `ComplianceRuleSummaryDto.severity` as the closed enum
  `[Info, Warning, Critical]`, document-only, matching `fixtures/compliance-rule-summary.v1.json`.
- Assert (in-spec + fixture) that `TopicTrendsResponse` already emits `trends` (+ `totalAnalyzed`) —
  a regression guard, not a change.
- Verify the emitted `PagedResult<T>` envelope matches `fixtures/paged-result-envelope.v1.json` and
  record the `PagedResultOf<T>` monomorphization as by-design.
- Preserve the ADR-0036 seam's document-only / AOT-safe / runtime-unchanged properties.

**Non-Goals:**

- **No change to `TopicTrendsResponse` in the host** — the fix is Web-side (the child change).
- **No change to `PagedResult`** — envelope is correct; monomorphization is codegen-inherent.
- **No runtime behavior change** — no wire body, status code, gating, or `JsonNumberHandling` change.
  `ComplianceRuleSummaryDto.Severity` stays a `string`-typed member (the server still writes plain
  strings; the transformer only narrows the *document's* declared type).
- **No new ADR authored here.** ADR-0037 MAY be authored later (see Open Questions); this change
  carries `decision_ref: Platform/ADR-0036`.
- **No cross-repo edits.** This is the HOST scaffold only; Web's shadow retirement is the child change.

## Decisions

### D1 — Restore `severity` as an enum via a sibling schema transformer, NOT a DTO type change

**Chosen:** add a second `IOpenApiSchemaTransformer` on the same `AddSchemaTransformer` seam
(`Program.cs:1633`, alongside `NumericSchemaTruthTransformer`) that narrows the emitted `severity`
property schema on `ComplianceRuleSummaryDto` to `enum: [Info, Warning, Critical]` (type `string`).
The DTO member stays `public string Severity` and stays registered in `ApiJsonContext`
(`Serialization/ApiJsonContext.cs:395-396`).

**Why over the alternative (change the DTO member to a C# enum):**

- **Symmetry with ADR-0036.** The residual shapes sit on top of ADR-0036 at the *same* seam; a sibling
  transformer keeps "make the document tell the truth" as a single, uniform document-only mechanism
  rather than splitting the fix between a DTO-level type change and a document-level one. One place to
  reason about the emitted contract.
- **Zero runtime risk.** A C# enum member changes what the serializer reads/writes and how the handler
  populates the field (the value flows up from Pro analytics as a string today). A document-only
  transformer cannot change any wire body, deserialization, or handler code — it only rewrites the
  built `OpenApiSchema`. This preserves the ADR-0036 "document states the truth, runtime stays as-is"
  invariant verbatim.
- **AOT-clean and no `[JsonSerializable]` churn.** The DTO remains a `string`-typed sealed record
  already in the source-gen context; no new enum type, no `JsonStringEnumConverter`, no source-gen
  context edits, no risk of an `IL2026`/`IL3050` regression. Transformers run over the OpenAPI object
  model — no reflection over user types (ADR-0022).

**Trade-off:** a document-only enum does not enforce the domain at deserialization (a malformed
inbound `severity` would still bind). This is acceptable and consistent with ADR-0036's document-only
posture — `severity` is a **response** field (produced by the server from Pro analytics), never an
inbound request field, so there is no untrusted deserialization path to guard. If a future request
DTO needed a validated severity, that would be modeled as a real enum at that DTO deliberately.

### D2 — `TopicTrendsResponse`: assert-only (regression guard), no host change

The host spec adds a requirement that the emitted `TopicTrendsResponse` schema matches
`fixtures/topic-trends-response.v1.json` (`trends` array of `{topic, occurrences, avgConfidence}` +
`totalAnalyzed`), with **no** `topics`/`from`/`to`. This is a captured-document assertion, not a
producer edit — it locks the already-correct shape so a future refactor cannot silently reintroduce
the `topics` name the Web shadow carried. The consumer fix (delete the shadow, repoint
`speech-analytics-page.tsx`) is the Web child change.

### D3 — `PagedResult` envelope: verify + rule by-design

The host spec adds a verification requirement that the emitted `PagedResult<T>` envelope matches
`fixtures/paged-result-envelope.v1.json` (`items`, `totalCount`, `page`, `pageSize`, `totalPages`,
`hasNextPage`, `hasPreviousPage`) and records that `openapi-typescript`'s `PagedResultOf<T>`
monomorphization is **by-design** — the emitted OpenAPI document has no reusable generic, so the
codegen legitimately expands one concrete envelope per element type. No producer change; giving the
"verify/close" decision a concrete verbatim fixture reference rather than a guess.

### D4 — Verification lane reuses the existing CI-runtime capture

All three assertions run against the same CI-runtime-captured document ADR-0035/ADR-0036 already
produce (`openapi-export` capability). No new CI job, no new dependency — the residual-shape checks
extend the existing `verify-openapi-fixture.py`-style verbatim-field assertion to these three
fixtures.

## Risks / Trade-offs

- **[The severity value the server emits at runtime is outside `{Info, Warning, Critical}`]** → the
  document would then advertise a closed enum the runtime can violate. Mitigation: the enum is sourced
  directly from `ComplianceSeverityBreakdownDto`'s three members (`CallAnalyticsEndpoints.cs:382-385`),
  which is the same producer's own severity domain; the transformer and a capture test assert the three
  values match the fixture verbatim. If Pro analytics ever adds a fourth severity, the fixture + enum
  must be extended in lockstep (a spec-visible change, not a silent drift).
- **[A second transformer on the shared seam interferes with `NumericSchemaTruthTransformer`]** →
  Mitigation: the severity transformer targets only the `severity` string property on one named
  schema; it touches no numeric schema, so the two transformers are orthogonal. Both are document-only
  and idempotent over the object model.
- **[Document-only enum gives false confidence that inbound severity is validated]** → Mitigation:
  documented in D1 — `severity` is response-only; no request path binds it. Recorded so a future
  maintainer does not mistake the document enum for request validation.
- **[Web narrows its shadow before Platform re-emits the enum]** → contract-ordering hazard.
  Mitigation: `impact.yaml` stages Platform (buildOrder 1) as a hard barrier before Web (buildOrder 2);
  the child change lands only after the corrected document is captured.

## Migration Plan

Not applicable at the host beyond the standard flow: land the sibling transformer + registration,
CI re-captures the corrected document, the three fixtures verify. Rollback = revert the transformer
registration line; the document reverts to the open-string `severity` with no runtime impact (the
DTO was never changed). Cross-repo staging is governed by `impact.yaml` (`/xr:apply`).

## Open Questions

- **ADR-0037?** The "severity as a document-only enum" ruling and the "PagedResult monomorphization is
  by-design" ruling are durable decisions that MAY warrant their own ADR (amending/extending
  Platform/ADR-0036). Deferred — this change carries `decision_ref: Platform/ADR-0036`; the ADR, if
  authored, lands with the implementation, not this scaffold.
