---
tier: GRANDE
owner: Harol
approver: Harol
stakeholder: Platform.Web frontend team (typed-client consumer), Platform API maintainers
decision_ref: Platform/ADR-0036
---

# Proposal: openapi-residual-contract-shapes (Platform host — make the emitted OpenAPI document tell the truth for the residual contract shapes)

## Why

The archived `openapi-numeric-schema-truth` train fixed the blanket `number | string` union but
left three residual contract-shape divergences that Platform.Web's `[Unreleased]` changelog logged
as three separate Platform contract bugs. Read-only `/xr:change` scouts (Platform + Platform.Web,
2026-07-25) reconciled all three against source and found they are **not symmetric**: the
advertised "3 Platform contract bugs" is really **1 genuine Platform producer fix + 1 Web-only
cleanup (no host change) + 1 verify-and-document (by-design)**. This change corrects the one real
producer divergence at the contract and pins the other two to golden fixtures so the host spec
records the reconciled truth (and the child Web change has a verbatim reference to consume).

Although the tier is GRANDE (cross-repo: Platform producer + Platform.Web consumer, per Platform's
proposal rule), the actual effort is small — **~<1 day**: one producer enum fix, one consumer
cleanup (in the Web child change), and one verification.

## What Changes

- **`ComplianceRuleSummaryDto.severity` — THE genuine Platform producer fix.**
  `Endpoints/CallAnalyticsEndpoints.cs:376` types `Severity` as a plain `string`; the intended
  contract is the literal union **`Info | Warning | Critical`** (cf. `ComplianceSeverityBreakdownDto`,
  `CallAnalyticsEndpoints.cs:382-385`, whose three integer members enumerate exactly those severities).
  Restore the enum **at the contract** via a sibling `IOpenApiSchemaTransformer` on the same
  `AddSchemaTransformer` seam as `OpenApi/NumericSchemaTruthTransformer.cs` (registered at
  `Program.cs:1633`) that narrows the emitted `severity` schema to the enum `[Info, Warning, Critical]`.
  Document-only, AOT-safe (no reflection over user types), the DTO stays a `string`-typed sealed
  record in `ApiJsonContext` (`Serialization/ApiJsonContext.cs:395-396`) so wire serialization and
  runtime leniency are unchanged. The corrected shape is pinned in
  `fixtures/compliance-rule-summary.v1.json` (`ruleId`, `ruleName`, `severity` ∈ {`Info`, `Warning`,
  `Critical`}, `occurrences`, `sessionsAffected`, `firstSeen`, `lastSeen`).

- **`TopicTrends` `topics`→`trends` — NOT a Platform change (documented no-op for the host).**
  Platform source is already `record TopicTrendsResponse(TopicTrendDto[] Trends, int TotalAnalyzed)`
  (`CallAnalyticsEndpoints.cs:355`), which serializes `trends`. The stale `topics` exists only in
  Web's hand-written shadow. The host spec **asserts** the emitted contract already matches
  `fixtures/topic-trends-response.v1.json` (a `trends` array of `{topic, occurrences, avgConfidence}`
  plus `totalAnalyzed`) — no producer work; the fix is Web-side (the child change).

- **`PagedResult` envelope — VERIFY / document by-design (not a fix).** `Platform.Core/PagedResult.cs`
  and Web's generated envelope already agree (`items`, `totalCount`, `page`, `pageSize`, `totalPages`,
  `hasNextPage`, `hasPreviousPage` — see `fixtures/paged-result-envelope.v1.json`). The only
  "divergence" is `openapi-typescript`'s `PagedResultOf<T>` monomorphization, inherent to codegen (no
  reusable generic in the emitted document). The host spec adds a **verification** requirement that the
  emitted envelope matches the fixture and rules the monomorphization **by-design** (no producer change).

- **No runtime behavior change** (document-only, same as `NumericSchemaTruthTransformer`). No new
  dependency, no new CI job.

## Capabilities

### New Capabilities

<!-- none -->

### Modified Capabilities

- `typed-response-schemas`: the emitted `ComplianceRuleSummaryDto` schema declares `severity` as the
  `Info | Warning | Critical` enum (via a sibling schema transformer on the same seam), not an open
  `string`; the `TopicTrendsResponse` schema already emits `trends` (asserted, no host change).
- `openapi-export`: the residual contract shapes (`compliance-rule-summary`, `topic-trends-response`,
  `paged-result-envelope`) are verified against their golden fixtures in the CI-runtime-captured
  document; the `PagedResult` monomorphization is ruled by-design.

## Impact

- **Cross-repo — see `impact.yaml`** (this change's
  `openspec/changes/openapi-residual-contract-shapes/impact.yaml`). Scope confirmed by `/xr:change`
  scouts: **producer** = Verbara.Platform (this host, the one real fix + two verify/assert shapes);
  **consumer** = Verbara.Platform.Web (regenerates `openapi.d.ts`, retires the `TopicTrendsResponse`
  and `ComplianceRuleSummaryDto` hand-written shadows, repoints the speech-analytics consumers).
  Verbara.Sdk, Verbara.Sdk.Pro, verbara-website: **out of scope** (none consumes the emitted
  OpenAPI document — same rationale as the sibling numeric-schema-truth change).
- **buildOrder**: Platform (1) lands the severity enum + CI re-exports the corrected document before
  Web (2) narrows its shadow — a hard contract barrier despite Web being decoupled (contract
  dependency, not NuGet).
- **decision_ref**: Platform/ADR-0036 (same "make the emitted OpenAPI document tell the truth" class
  as the archived `openapi-numeric-schema-truth` change; the residual shapes sit on top of ADR-0036 at
  the same `IOpenApiSchemaTransformer` seam). A follow-up **ADR-0037** MAY record the "severity as
  enum" ruling and the "PagedResult monomorphization is by-design" ruling as their own durable record
  (not authored by this change).
