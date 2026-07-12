# Tasks: openapi-typed-client (Platform host — OpenAPI export artifact)

## 1. ADR + build-time export mechanism (Phase A)

- [x] 1.1 Author `docs/decisions/0035-openapi-typed-client-contract.md` (ADR-0035) — records the
      build-time-export decision (design.md D1), the CI job placement decision (D2), the
      fixture-verification approach (D3), and the producer/consumer boundary with
      Platform.Web's `web/openapi-typed-client` child change. Resolves the `Platform/ADR-0035`
      forward-reference seeded in `impact.yaml` by `/xr:change`.
      **DEVIATION:** implementation found build-time export infeasible for this Program.cs (its
      ~28 `IHostedService`s require a live Postgres for reasons unrelated to OpenAPI — see
      ADR-0035 for the full experimental trace) and pivoted to CI-runtime host capture. The ADR
      as authored records BOTH the original decision and the pivot, per the apply-time deviation
      protocol; `design.md`/`spec.md` were updated in-change to match.
- [x] 1.2 ~~Add `Microsoft.Extensions.ApiDescription.Server` to `Directory.Packages.props`~~ —
      **NOT DONE, by design after the pivot.** The package was added, proven infeasible (host
      cannot reach a request-serving state without a live Postgres — see ADR-0035), and reverted.
      `Directory.Packages.props` and `Verbara.Platform.Api.csproj` are unchanged from `main`.
- [x] 1.3 ~~Confirm `dotnet build ... ` produces the generated OpenAPI document in the build
      output~~ — **superseded by the pivot**: confirmed instead that
      `dotnet build Verbara.Platform.slnx -c Release` succeeds with zero warnings (verification
      gate), and that the CI-runtime-captured document (task 2.1) is produced independent of
      `Platform:OpenApi:Enabled`'s default and `ASPNETCORE_ENVIRONMENT`, per the updated spec
      requirement.

## 2. CI export + artifact publication (Phase B)

- [x] 2.1 Add export steps to the `build-and-test` job in `.github/workflows/ci.yml`: a
      `postgres:18-alpine` `services:` container (ephemeral, job-scoped), an "Export OpenAPI
      document (CI-runtime capture)" step that starts the built `Verbara.Platform.Api.dll` with
      `Platform:OpenApi:Enabled=true` against that container, polls `/openapi/v1.json` until
      ready, saves the response, and stops the host, and an `actions/upload-artifact` step
      publishing `openapi-document-${{ github.sha }}` — scoped to the triggering commit/run.
      **DEVIATION from the original task wording** ("after the existing `dotnet build` step"
      assumed a build-output file to upload) — the step instead runs the host briefly; see
      ADR-0035.
- [x] 2.2 Verified locally end-to-end (the exact CI step logic, run outside GitHub Actions against
      an ephemeral `postgres:18-alpine` Docker container standing in for the `services:` block):
      the host reaches "Application started", `/openapi/v1.json` returns HTTP 200 with a
      182-schema/324-path document, and its content matches the document task 1.3's confirmation
      step observed. Real CI-run verification (task 4.3) still applies for the GitHub Actions
      environment itself.

## 3. Fixture verification (Phase C)

- [x] 3.1 Generated the real OpenAPI document via CI-runtime capture (post-pivot; see task 2.1)
      and extracted the `CsatResponseDto` schema fragment
      (`components.schemas.CsatResponseDto`, referenced from
      `/api/v{version}/analytics/csat/queues/{queueId}` — see 3.2's path-template note).
- [x] 3.2 Compared that fragment against `fixtures/openapi-document.v1.sample.json` via the new
      `scripts/verify-openapi-fixture.py` (task 3.3's "repeatable script/test", not a one-off
      eyeball) — field names match exactly for all 6 fields (`queueName`, `channel`,
      `totalResponses`, `averageRating`, `rangeStart`, `rangeEnd`); illustrative numeric/date-time
      format differences (e.g. `totalResponses`'s `int64` in the fixture vs. the real document's
      `int32` + integer/string union) are expected and not a mismatch, per the fixture's
      documented intent. **No fixture update was required.** One additional, previously
      undocumented difference found: the real document's path key uses `/api/v{version}/...`
      (Asp.Versioning.Http's URL-segment-version template placeholder), not the fixture's
      literal `/api/v1/...` — an envelope/path-key detail, not a `CsatResponseDto` field
      difference; documented in `specs/openapi-export/spec.md` and does not block verification
      (the script compares the schema fragment directly, not the path string).
- [x] 3.3 No fixture update was needed (3.2), so no diff to record under that heading; the
      verification script itself (`scripts/verify-openapi-fixture.py`) is the "repeatable
      script/test" this task asked for, and the path-template + build-time-vs-CI-runtime findings
      are recorded in ADR-0035's "Verification" section as the documented baseline for future
      drift.

## 4. Verification (Phase D)

- [x] 4.1 `dotnet build Verbara.Platform.slnx -c Release` — zero warnings, zero errors.
- [x] 4.2 `dotnet test Verbara.Platform.slnx --no-build -c Release
      --filter "FullyQualifiedName!~Storage.Postgres.Tests&FullyQualifiedName!~Identity.Redis.Tests"`
      — full suite green: 1628 tests, 1628 passed, 0 failed. No new test failures (no runtime
      endpoint behavior changed; the two `ApiJsonContext` additions are additive schema metadata).
- [x] 4.3 CI green on the PR (the new export/upload steps included) — **CLOSED BY EVIDENCE**: the
      real PR #149 GitHub Actions run executed the export/upload steps added in task 2.1 and
      produced the artifact `openapi-document-e43c0ab43deee2ee526b66bbd9d40d8b366619c4`
      (24,892 bytes), confirming the CI-runtime capture mechanism (ADR-0035) works end-to-end in
      the real pipeline, not just the local simulation (task 2.2). PR #149 merged to main at
      2026-07-12T22:24:54Z.
- [x] 4.4 `npx -y @fission-ai/openspec@1.6.0 validate --all --strict --no-interactive` — green
      (12 passed, 0 failed).
