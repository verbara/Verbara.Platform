# Tasks: openapi-numeric-schema-truth

> Host (producer) tasks for the cross-repo `openapi-numeric-schema-truth` change. Ordered by
> dependency: the transformer + registration first, then verification/capture, then the ADR/CHANGELOG.
> The captured corrected document (task 3.2) is the Stage-2 (Web) handoff artifact.

## 1. The transformer

- [x] 1.1 Add `NumericSchemaTruthTransformer` (`IOpenApiSchemaTransformer`) in
  `src/Verbara.Platform.Api/OpenApi/`: for any schema whose `type` is a numeric+`string` union
  (Microsoft.OpenApi 2.9.0 `JsonSchemaType` `[Flags]` enum — `Integer|String` / `Number|String`,
  incl. the nullable `…|Null` case), clear ONLY the `String` bit; preserve numeric flag, `Null`
  (nullability), and `Format`. Pure `string` and pure numeric schemas untouched. Document-only,
  AOT-safe, blanket over int32/int64/double/float with an empty exemption list + the >2^53 rider comment.
- [x] 1.2 Register it at `Program.cs` — `AddOpenApi(o => o.AddSchemaTransformer<NumericSchemaTruthTransformer>())`.

## 2. Tests

- [x] 2.1 Unit-test the transformer (`NumericSchemaTruthTransformerTests`): integer/string → integer,
  number/string → number, nullable union preserves `Null`, int64 covered (no exemption), format
  preserved; pure string / pure integer / `null` type / object schema all unchanged.
- [x] 2.2 Integration-test the emitted document (`NumericSchemaTruthCaptureTests`): boot the host
  in-memory (`Platform:OpenApi:Enabled=true`), fetch `/openapi/v1.json`, assert no numeric+string
  union survives (whitespace-insensitive).

## 3. Fixture / verification / handoff

- [x] 3.1 Correct the golden fixture `fixtures/openapi-numeric-schema.v1.json` to the real captured
  shape (`DashboardKpisDto.avg*Ms` are `double`; nullable numerics as OpenAPI-3.1 `["null","integer"]`);
  tighten `scripts/verify-openapi-fixture.py` to fail on any surviving numeric+string union (nullable
  numerics stay legitimate) — existing name-check green preserved.
- [x] 3.2 Capture the corrected document to `fixtures/openapi-document.corrected.json` (Stage-2 Web
  handoff); confirm zero `["integer","string"]`/`["number","string"]` unions.

## 4. Records

- [x] 4.1 Author ADR-0036 (`docs/decisions/0036-openapi-numeric-schema-truth.md`) — amends ADR-0035:
  transformer + Microsoft.OpenApi API + document-only rationale (not Strict) + the >2^53 rider.
- [x] 4.2 Add the `[Unreleased]` CHANGELOG entry.
