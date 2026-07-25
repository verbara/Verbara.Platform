## ADDED Requirements

### Requirement: The residual contract-shape fixtures are verified against the real emitted document

The three residual contract-shape golden fixtures — `fixtures/compliance-rule-summary.v1.json`,
`fixtures/topic-trends-response.v1.json`, and `fixtures/paged-result-envelope.v1.json` — SHALL be
verified, as part of this change's validation, against the corresponding schema fragments of the real
CI-runtime-captured OpenAPI document, so they cannot silently drift from the emitted contract (the
same verbatim-fixture-citation guard the `openapi-export` capability already applies to
`CsatResponseDto`). For each fixture, the emitted schema's property names MUST match the fixture
verbatim:

- `ComplianceRuleSummaryDto` → `ruleId`, `ruleName`, `severity` (enum `Info | Warning | Critical`),
  `occurrences`, `sessionsAffected`, `firstSeen`, `lastSeen`
- `TopicTrendsResponse` → `trends` (array of `{topic, occurrences, avgConfidence}`), `totalAnalyzed`
- `PagedResult<T>` envelope → `items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`,
  `hasPreviousPage`

#### Scenario: The residual-shape fixtures match the captured document verbatim

- **GIVEN** the CI-runtime-captured OpenAPI document and the three residual-shape fixtures
- **WHEN** the fixtures' schema fragments are compared against the corresponding fragments in the real
  document
- **THEN** each fixture's property names match the emitted schema verbatim — `ComplianceRuleSummaryDto`
  with `severity` as the closed enum, `TopicTrendsResponse` with `trends`/`totalAnalyzed`, and the
  `PagedResult<T>` envelope with its seven keys — and a missing field or name mismatch fails the check

### Requirement: The PagedResult monomorphization is verified and ruled by-design

The emitted `PagedResult<T>` envelope SHALL be verified to match `fixtures/paged-result-envelope.v1.json`
(`items`, `totalCount`, `page`, `pageSize`, `totalPages`, `hasNextPage`, `hasPreviousPage`) verbatim, and
`openapi-typescript`'s `PagedResultOf<T>` monomorphization (one concrete envelope emitted per element
type) SHALL be recorded as **by-design**, not a contract defect: the emitted OpenAPI document exposes no
reusable generic, so expanding one concrete envelope per element type is the correct codegen behavior.
No producer change to `Platform.Core/PagedResult.cs` is made by this capability.

#### Scenario: The paged-result envelope matches the fixture and monomorphization is by-design

- **GIVEN** the CI-runtime-captured OpenAPI document
- **WHEN** an endpoint returning a `PagedResult<T>` is inspected and its envelope compared against
  `fixtures/paged-result-envelope.v1.json`
- **THEN** the envelope declares exactly `items`, `totalCount`, `page`, `pageSize`, `totalPages`,
  `hasNextPage`, `hasPreviousPage` (verbatim), matching the fixture
- **AND** the consumer's `PagedResultOf<T>` per-type expansion is ruled by-design (no reusable generic
  in the document), so no producer change is required

## Architectural Risk

**Level:** LOW

**Affected:** `Verbara.Platform` (the CI-runtime OpenAPI capture and its fixture-verification step for
the `analytics` group and the shared `PagedResult<T>` envelope). Downstream, `Verbara.Platform.Web`
consumes the verified shapes when regenerating `openapi.d.ts` (child change). No effect on
`Verbara.Sdk` or `Verbara.Sdk.Pro`.

**Mitigation:** the verification reuses the existing CI-runtime capture (ADR-0035/ADR-0036) and the
established verbatim-field assertion — no new CI job, no new dependency. Two of the three shapes
(`TopicTrendsResponse`, `PagedResult<T>`) are assert-only against already-correct emitted schemas; the
only shape whose contract changes is `ComplianceRuleSummaryDto.severity`, narrowed document-only by the
sibling transformer (see the `typed-response-schemas` delta). Fixtures pin every asserted field name
verbatim so drift fails the check.
