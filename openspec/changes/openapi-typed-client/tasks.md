# Tasks: openapi-typed-client (Platform host — OpenAPI export artifact)

## 1. ADR + build-time export mechanism (Phase A)

- [ ] 1.1 Author `docs/decisions/0035-openapi-typed-client-contract.md` (ADR-0035) — records the
      build-time-export decision (design.md D1), the CI job placement decision (D2), the
      fixture-verification approach (D3), and the producer/consumer boundary with
      Platform.Web's `web/openapi-typed-client` child change. Resolves the `Platform/ADR-0035`
      forward-reference seeded in `impact.yaml` by `/xr:change`.
- [ ] 1.2 Add `Microsoft.Extensions.ApiDescription.Server` to `Directory.Packages.props` (pinned
      version matching the installed `Microsoft.AspNetCore.OpenApi 10.0.5` family) and reference
      it from `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj` with
      `<OpenApiGenerateDocuments>true</OpenApiGenerateDocuments>`; verify it is a build/analyzer-only
      reference that does not ship in the AOT-published output (no effect on `IsAotCompatible`).
- [ ] 1.3 Confirm `dotnet build Verbara.Platform.slnx -c Release` produces the generated OpenAPI
      document in the build output for `Verbara.Platform.Api`, independent of
      `Platform:OpenApi:Enabled` / `ASPNETCORE_ENVIRONMENT`, with zero new build warnings
      (TreatWarningsAsErrors stays green).

## 2. CI export + artifact publication (Phase B)

- [ ] 2.1 Add an `actions/upload-artifact` step to the `build-and-test` job in
      `.github/workflows/ci.yml` (after the existing `dotnet build Verbara.Platform.slnx -c
      Release` step) that publishes the generated OpenAPI document as a named, versioned CI
      artifact scoped to the triggering commit/run.
- [ ] 2.2 Verify the artifact is downloadable from a completed `build-and-test` run (manual CI
      run or PR check) and its content matches the document produced by task 1.3.

## 3. Fixture verification (Phase C)

- [ ] 3.1 Generate the real OpenAPI document via the task 1.2 build-time export and extract the
      `CsatResponseDto` schema fragment (`components.schemas.CsatResponseDto` and its reference
      under `/api/v1/analytics/csat/queues/{queueId}`).
- [ ] 3.2 Compare that fragment against `fixtures/openapi-document.v1.sample.json` — field names
      and types must match exactly; update the fixture only if a genuine mismatch is found
      (illustrative numeric/date-time formats in the fixture are expected to differ and are not a
      mismatch, per the fixture's documented intent in `impact.yaml`).
      **Note:** at propose-time, comparing the fixture (Version 3.0.1 style with an
      `"analytics/csat"`-only path) against `Program.cs`'s live `AddOpenApi()` registration found
      no discrepancy in the `CsatResponseDto` shape — this task re-verifies against the actual
      build-time-exported document once task 1.2 lands, since propose-stage does not run the
      build-time generator.
- [ ] 3.3 If the fixture required an update in 3.2, record the diff and reason in the ADR-0035
      "verification" section (task 1.1) so future drift has a documented baseline.

## 4. Verification (Phase D)

- [ ] 4.1 `dotnet build Verbara.Platform.slnx -c Release` — zero warnings
      (TreatWarningsAsErrors=true, WarningLevel=9999).
- [ ] 4.2 `dotnet test Verbara.Platform.slnx --no-build -c Release` — full suite green (no
      runtime behavior changed, so no new test failures expected; add a regression test only if
      the build-time export step introduces a testable seam, e.g. a project-level MSBuild
      target test).
- [ ] 4.3 CI green on the PR (the new export/upload step included) — confirms task 2.1/2.2 work
      end-to-end in the real pipeline, not just locally.
- [ ] 4.4 `npx -y @fission-ai/openspec@1.6.0 validate --all --strict --no-interactive` green.
