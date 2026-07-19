# openapi-export Specification (delta)

## ADDED Requirements

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
