## 1. Phase A — Foundation (batch)

- [x] 1.1 Add a `ToUtcInstant()` extension (`DateTimeOffset` → `DateTimeOffset` with `Offset == TimeSpan.Zero`, `DateTimeOffset?` overload passing through `null`) in `src/Verbara.Platform.Core/`. Implement as `.ToUniversalTime()` — never `DateTime.SpecifyKind` (design D1/D3). Add XML docs stating WHY: Npgsql MODERN rejects any non-zero `Offset` on a `timestamptz` write.
- [x] 1.2 Unit-test the helper: offset-0 input is a no-op, `-05:00` input normalises to the same instant at `+00:00`, `null` passes through, `DateTimeOffset.MinValue`/`MaxValue` do not overflow.
- [x] 1.3 Record the frozen ingress inventory in this change's folder as `ingress-inventory.md` (30 `Parse`/`TryParse` + 24 query params + 5 body DTOs, file:line each) so tasks 2.x and the 5.x gate can be checked against a fixed list rather than a re-grep.
- [x] 1.4 Update `proposal.md` frontmatter `tier: PEQUEÑO` → `tier: MEDIANO` (design "Risks / Trade-offs": removal + ingress sweep + CI gate + `/setup` guard exceeds PEQUEÑO).

## 2. Phase B — Ingress normalisation sweep (focused, switch still ON)

> Order matters (design "Migration Plan" step 1): this whole section is a **no-op under LEGACY**, so it lands and can be verified independently, before the switch is touched.

- [x] 2.1 Normalise the 12 `DateTimeOffset.Parse` sites in `Endpoints/AnalyticsEndpoints.cs` (48, 49, 127, 128, 314, 315, 461, 462, 491, 492, 522, 523). These feed Pro's compiled analytics stores, so this is the cross-boundary fix — a read path that would 500 the dashboards, not just a write.
- [x] 2.2 Normalise the 6 `Parse` sites in `Endpoints/CallAnalyticsEndpoints.cs` (45, 46, 122, 123, 204, 205).
- [x] 2.3 Normalise the 5 `Parse`/`TryParse` sites in `Endpoints/CampaignEndpoints.cs` (140, 141, 444, 553, 554) — campaign create/update and callback scheduling into Pro's compiled `PostgresCampaignStore`.
- [x] 2.4 Normalise the remaining `Parse`/`TryParse` sites: `Endpoints/ConversationEndpoints.cs:686`, `Services/ConversationTimeoutWorker.cs:168`, `Services/CallbackRescueWorker.cs:192`, `Verbara.Platform.Typification/Validation/DefaultTypificationValidator.cs:403`, `Verbara.Platform.Realtime/Endpoints/AdminRealtimeAuditEndpoint.cs:42`.
- [x] 2.5 Normalise `Verbara.Platform.Channels.Email/SimpleEmailParser.cs:34` — the RFC-2822 `Date:` header is attacker-controlled and routinely carries a non-zero offset; treat it as the highest-risk single ingress site and add a parser unit test with a `-0500` header.
- [x] 2.6 Normalise the 16 `[FromQuery] DateTimeOffset?` params: `CreditLedgerEndpoints.cs` (246, 247), `PartnerBillingEndpoints.cs` (160, 161), `ManagementBillingEndpoints.cs` (300, 301, 316, 317), `PartnerRevenueEndpoints.cs` (27, 28, 53, 54), `GdprEndpoints.cs` (154, 155), `ManagementImpersonationEndpoints.cs` (493, 494).
- [x] 2.7 Normalise the 8 unattributed query params: `AuditEndpoints.cs` (25, 26), `Endpoints/Audit/AuditEndpoints.cs` (66, 67, 108, 109), `AuthAdminEndpoints.cs` (91, 92). ASP.NET Core's binder uses `AssumeUniversal`, which only supplies a *missing* offset — an explicit `-05:00` survives verbatim.
- [x] 2.8 Normalise the 5 body DTOs at their assignment sites: `Dtos/CsatResponseRequest.CapturedAt` (**anonymous public endpoint** — normalise in `CsatResponseEndpoints.cs:237`), `PromoGrantRequest.ExpiresAt` (`CreditLedgerEndpoints.cs:389` → `:112`), `CreateRateCardRequest.EffectiveFrom`/`EffectiveTo` (`ManagementBillingEndpoints.cs:745-746` → `:73-74`, `:120-121`), `GenerateInvoiceRequest.PeriodStart`/`PeriodEnd` (`:760` → `:206`), `AddDncEntryRequest.ExpiresAt` (`DncListEndpoints.cs:279` → `:175-181`).
- [x] 2.9 Verify the sweep is a no-op under LEGACY: `dotnet build` 0 warnings and `dotnet test Verbara.Platform.slnx` green **with the switch still declared**. This proves Phase B is independently revertable.

> **2.9 result (switch still declared):** `dotnet build Verbara.Platform.slnx` → 0 warnings / 0 errors.
> `dotnet test Verbara.Platform.slnx` → every project green **except** `Storage.Postgres.Tests`, which
> fails with `NpgsqlException: Exception while reading from stream ---- Connection reset by peer` at the
> container fixture. Pre-existing and unrelated: the sweep touched **zero** files under
> `Storage.Postgres`, the error is transport-level rather than an assertion, and the failure count varies
> run-to-run over identical code (27 / 136 / 11 / 6-of-6 in four consecutive runs). This is the open
> `fix-testcontainers-tcp-readiness` change (0/24 tasks). It does **not** gate this change, but §6.2 cannot
> be honestly signed off until that lane is fixed — see the note there.

> **Inventory correction found by the sweep:** `PartnerBillingEndpoints.cs:70-71` and `:103-104` carry the
> same rate-card body pattern into the same `IRateCardStore.SaveAsync` and were MISSING from the task 1.3
> inventory (which located body DTOs by a hand-picked property-name grep). Fixed, and `ingress-inventory.md`
> now records the correction plus the systematic second pass that closed the class. Totals: 59 → **61 sites**.

## 3. Phase C — Remove the switch (the root-cause commit)

- [x] 3.1 Delete the `ItemGroup` at `src/Verbara.Platform.Api/Verbara.Platform.Api.csproj:50-53` (the `RuntimeHostConfigurationOption` and its ADR-0022 comment block).
- [x] 3.2 Confirm no other declaration survives anywhere: `grep -rn EnableLegacyTimestampBehavior` across `src/`, `tests/`, `docker/`, and any `runtimeconfig.template.json` must return 0 hits, and the built `Verbara.Platform.Api.runtimeconfig.json` must not contain the key.
- [x] 3.3 Simplify `Storage.Postgres/Stores/PostgresBotConfigStore.cs:119` — `new DateTimeOffset(DateTime.SpecifyKind(created_at, DateTimeKind.Utc))` was silently shifting `CreatedAt` by the host offset under LEGACY. It becomes correct automatically under MODERN, but must not be left as a misleading artefact. It is the repo's only `SpecifyKind` site.
- [x] 3.4 Spot-check that the 54 `new DateTimeOffset(x, TimeSpan.Zero)` sites now compile and behave correctly without edits (design D1: correct by construction under `Kind=Utc`). No mechanical patch to those sites is in scope.

  **Result: D1 holds. Zero edits required. Two corrections to this change's own framing:**

  - **The count is 50, not 54.** The "54" was a line-grep that over-counted by 5 *seven-argument*
    `DateTimeOffset(y, m, d, h, m, s, offset)` constructions, which take no `DateTime` and cannot
    throw on any `Kind` (`PartnerBillingEndpoints.cs:181`, `ManagementBillingEndpoints.cs:307,326`,
    `CreditLedgerEndpoints.cs:260`, `Billing/BillingPeriod.cs:25`), and under-counted by 1 — the
    site this change itself created at `PostgresBotConfigStore.cs:119`. No multi-line constructor
    forms exist, so the grep is complete.
  - **Classification of the 50:** 46 are Postgres store projections, every one resolved
    mechanically to its `GetDateTime`/`GetDateTimeOrNull` call, its table, and its column type.
    **Category (b) — a `timestamp without time zone` column — is EMPTY:** all 135 timestamp columns
    across the 17 migrations are `TIMESTAMPTZ`, including the two added by `ALTER TABLE`
    (`audit_entries.retain_until`, `typification_submissions.corrected_at`). The remaining 4 are
    `ScheduledReportEndpoints.cs:84,156` and `ReportSchedulerService.cs:231,290`, all
    `new DateTimeOffset(schedule.GetNextOccurrence(now.UtcDateTime), TimeSpan.Zero)`. Those are
    safe, but **not for D1's reason** — no Npgsql read is involved; NCrontab 3.3.3 propagates
    `baseTime.Kind` (verified by decompilation, not assumed), and the input is `IClock.UtcNow
    .UtcDateTime`. They were never part of the bug and would break only if the argument changed to
    `now.DateTime`/`now.LocalDateTime`.

  **`SpecifyKind` verification:** the fix at `PostgresBotConfigStore.cs:119` is the only one that
  ever existed. `grep -rn "SpecifyKind" src/` now returns a single hit, and it is prose inside the
  XML doc comment on `UtcInstantExtensions.cs:27`. Nothing in `Verbara.Sdk` or `Verbara.Sdk.Pro`.

  **Wider population D1 does not name (no action needed, recorded so the next reader is not
  surprised):** ~60 further store projections assign a reader-sourced `DateTime` straight to a
  `DateTimeOffset` property via the implicit conversion, which accepts any `Kind` and therefore
  never threw. Under LEGACY they silently produced `Offset=-05:00` on a UTC-5 host — the correct
  instant with the wrong wire string. The same removal fixes them to `+00:00`. This is the concrete
  instance of the wire-format change already recorded in `CHANGELOG.md` under 6.7.

## 4. Phase D — `/setup` retryability (orthogonal, any order)

- [x] 4.1 Change the guard in `src/Verbara.Platform.Api/Endpoints/SetupEndpoints.cs:31-33` from `tenantStore.GetHostTenantAsync(ct)` to evidence that setup **finished** — the existence of a platform user (design D6).
- [x] 4.2 Make the six-write sequence tolerate re-entry over a half-written state: the `platform` tenant id is deterministic, so a retry must adopt the existing tenant rather than fail on a duplicate insert.
- [x] 4.3 Update the `NON-ATOMIC FIRST-RUN WINDOW` comment block at `SetupEndpoints.cs:79-90` to describe the new retry semantics instead of the old dead end, and reconcile `docs/specs/2026-05-30-setup-multitenant-platform-customer.md` with the change.
- [x] 4.4 Test: setup fails after the host tenant is written but before any user, then a retry with valid input succeeds and returns the platform user — asserting **no** `409 "Platform already initialized."` (spec scenario "Setup can be retried after a mid-way failure").

## 5. Phase E — Regression coverage + CI gate (batch)

- [x] 5.1 Extend `scripts/check-endpoint-invariants.py` to fail if `Npgsql.EnableLegacyTimestampBehavior` appears in any `.csproj`, `runtimeconfig.template.json`, or `runtimeconfig.json` — the switch cannot return silently (design D4). This runs in the existing required **Invariant Gates** job.
- [x] 5.2 Extend the same script to reject `DateTime.SpecifyKind(..., DateTimeKind.Utc)` applied to a Postgres-reader-sourced value (the corrupting pattern from design D1).
- [x] 5.3 Unit-test both new invariant checks in the existing `coverage-scripts` job, matching how the current checks are tested.
- [x] 5.4 Add a Storage.Postgres round-trip test under a non-UTC `TZ` asserting the read yields `Kind=Utc` / `Offset == TimeSpan.Zero` and the projection does not throw (spec scenario "A store projection survives a non-UTC process timezone").
- [x] 5.5 Add a write test binding a `DateTimeOffset` with a non-zero `Offset` through an ingress path, asserting it is normalised rather than rejected — the design D2 contract, and the test that would have caught the regression removal introduces.
- [x] 5.6 Add a `QueueDistributionWorker` test asserting a distribution cycle completes without logging `Distribution cycle failed` under a non-UTC `TZ` (spec scenario "The background distribution loop does not fail on a non-UTC host").

  Added `tests/Verbara.Platform.Api.Tests/Workers/QueueDistributionWorkerCycleFailureLoggingTests.cs`
  with two tests: one drives the real `ExecuteAsync` loop through 3+ fully-wired successful cycles
  and pins zero `Distribution cycle failed` records (asserting the switchboard was actually offered
  a conversation, so "no failure logged" is not a statement about an empty no-op); the other makes
  the store throw for exactly 2 cycles and pins **exactly 2** records, still 2 after 3 more clean
  cycles — the spec's "does not recur every cycle for the process lifetime" clause, previously
  untested, and the positive control proving the log capture matches the real message text.

  **Vacuity disclosure — this test would NOT fail with the switch reinstated, and 5.7 must not
  expect it to.** `Api.Tests` cannot reach Npgsql's converter selection: it has one
  `ProjectReference`, is container-free by design, and every store on the cycle path is an
  NSubstitute double, so no `NpgsqlDataReader` is ever constructed in that assembly. Two
  alternatives were evaluated and rejected on the merits rather than worked around: feeding the
  stores `Local`-kind values is impossible because `DateTimeOffset.DateTime` always returns
  `Kind == Unspecified` and the store interfaces expose `DateTimeOffset`, not `DateTime`; and
  mutating `TZ`/`TimeZoneInfo` adds no signal while racing ~1750 parallel tests, because `grep`
  over `src/` returns **zero** hits for `DateTime.Now`, `DateTimeOffset.Now`, `TimeZoneInfo.Local`
  and `ToLocalTime()` — the process timezone is not observable anywhere on this path.

  That last finding is the real answer to the spec scenario: the worker path is timezone-independent
  *by construction*, not by test. The projection layer it used to fail through is covered by 5.4/5.5
  (container-backed) and the switch itself by gate #10. The scenario is satisfied in aggregate; this
  test carries the worker's own error-path and cycle-completion half, which nothing covered before.
- [x] 5.7 Confirm these tests are meaningful rather than vacuous: with host and tests now sharing MODERN semantics, verify 5.4–5.6 actually **fail** when the switch is temporarily reinstated locally, then remove the reinstatement.

  **Scope amended during execution: the proof applies to 5.4–5.5 only, not 5.6.** See the vacuity
  disclosure under 5.6 — `Api.Tests` structurally cannot observe the Npgsql converter, so demanding
  that 5.6 fail under a reinstated switch would be demanding a test that lies about what it
  exercises. Amending the check is the honest resolution; weakening 5.4/5.5 to match is not.

  **Proof, re-run and witnessed directly (not delegated):** the `RuntimeHostConfigurationOption`
  was temporarily added back to the *test* csproj — vstest propagates the test assembly's
  runtimeconfig to the testhost, so the switch genuinely applies — and the suite was run with
  **`TZ=UTC`**, the CI-runner case:

  ```
  Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
  ```

  Both tests go red, on a UTC process, with `Expected ... Kind to be DateTimeKind.Utc ... but found
  DateTimeKind.Local`. That is exactly the spec clause "it fails on a UTC CI runner too, rather than
  only on a developer's machine". In the same reinstated state `scripts/check-endpoint-invariants.py`
  exits **1** (gate #10 trips), so the switch cannot return silently even if someone deletes the
  tests. Reinstatement reverted; `grep -c EnableLegacyTimestampBehavior` on the test csproj is `0`,
  the gate reports OK, and the two tests are green again.

## 6. Verification

- [x] 6.1 `dotnet build Verbara.Platform.slnx` — **0 warnings, 0 errors** (`TreatWarningsAsErrors`, `WarningLevel 9999`).
- [x] 6.2 `dotnet test Verbara.Platform.slnx` green, including the container-backed `Storage.Postgres.Tests` and `Identity.Redis.Tests` lanes (currently report-only in CI — run them locally and read the results, do not trust a green summary).
- [x] 6.3 Boot the **published Native AOT** binary in `Production` against a real Postgres on a **non-UTC host** (`TZ=America/Bogota`) and confirm the two reported symptoms are gone: no repeating `Distribution cycle failed`, and `POST /api/v1/setup` completes on a fresh database. This is the reproduction that opened the change; a JIT run does not substitute for it.
- [x] 6.4 Exercise one Pro-backed analytics endpoint and one campaign write with an explicit `-05:00` offset in the query string / body, confirming they succeed rather than throwing `Cannot write DateTimeOffset with Offset=…`.
> **6.2 result:** 35 test projects, **0 failures**. `Storage.Postgres.Tests` 264 passed (the lane
> that was flaky before `fix-testcontainers-tcp-readiness`); `Identity.Redis.Tests` green. Run
> locally with the containers actually starting, not read off a CI summary.

> **6.3 result — both symptoms gone.** Published artefact verified genuinely native
> (`ELF 64-bit LSB pie executable … stripped`, 79,291,408 bytes, **0 managed Verbara DLLs**
> alongside it). Booted as PID 2007846 with `ASPNETCORE_ENVIRONMENT=Production`,
> `/proc/<pid>/environ` confirming `TZ=America/Bogota`, against a real PostgreSQL 18 container
> (`verbara-tz-check`, port 55432) on a **fresh** database.
> - **Symptom 2 cleared:** `POST /api/v1/setup` returned **HTTP 201** on the fresh database —
>   previously it half-completed, returned 400, and wedged the install behind
>   `409 "Platform already initialized."`.
> - **Symptom 1 cleared:** `Distribution cycle failed` occurrences in the boot log: **0**, over
>   8m12s of uptime. `DistributionOptions.PollIntervalMs` defaults to `2000`, so that is
>   **~245 consecutive clean cycles**; the original defect failed *every* cycle, in a loop.
>   Postgres `xact_commit` on the `platform` database advanced by **118 in a 12s sample window**,
>   proving the background workers really were polling rather than silently parked.
> - Also **0** occurrences of `UTC Offset of the local dateTime`, `Cannot write DateTimeOffset`,
>   and `ArgumentException` anywhere in the log. The only `fail:` lines are three licensing
>   messages, expected on an unlicensed host and unrelated to timezone handling.

> **6.4 — scope amended; the Pro-gated half is environmentally blocked, the mechanism is proven.**
> **What blocked it.** Every Pro-backed surface is behind `LicenseGateMiddleware`:
> `AnalyticsEndpoints`, `AnalyticsLiveEndpoints`, `QueueMetricsEndpoints`, `CallAnalyticsEndpoints`
> (`LicenseFeature.Analytics`), `CampaignEndpoints`, `DialerSettingsEndpoints`, `DncListEndpoints`,
> `CallerIdPoolEndpoints`, `HolidayCalendarEndpoints` (`LicenseFeature.Dialer`), plus Csat,
> Realtime and Typification. `GET /api/v1/analytics/dashboard?from=…-05:00&to=…-05:00` returns
> **HTTP 402 `license-required`**, not a timezone error. Enabling them needs a **signed `.lic`**
> issued against the official ECDSA trust anchor; no license file exists in this repo or on this
> machine, and minting one is not available here. The two named endpoints are therefore
> unreachable by any HTTP call, for a reason unrelated to this change.
>
> **Substituted live proof (same binary, same non-UTC host, a real ingress → Postgres write).**
> `POST /api/v1/management/credit-ledger/promo-grant` carries a caller-supplied
> `DateTimeOffset? ExpiresAt` and is **not** license-gated. Posted
> `{"tenantId":"tzcheck","amount":25.50,"idempotencyKey":"tz-offset-probe-1","expiresAt":"2026-12-31T23:59:59-05:00"}`
> → **HTTP 200** `{"balance":25.500000}`. Reading the row straight out of Postgres:
> `credit_lot.expires_at = 2027-01-01 04:59:59+00` — exactly `2026-12-31T23:59:59-05:00`
> converted. **The instant is preserved, not shifted and not relabelled**, which is precisely what
> D3's `.ToUtcInstant()` (convert, never `SpecifyKind`) is there to guarantee. The read side is
> covered too: `GET /api/v1/admin/audit/events?from=…-05:00&to=…-05:00` returned **HTTP 200**,
> binding non-zero-offset values into a `timestamptz` `WHERE` clause without throwing.
> This exercises the exact failure mode 6.4 targets: `Cannot write DateTimeOffset with Offset=…`
> is raised by **Npgsql's parameter writer**, in the shared Npgsql assembly, at bind time —
> whether the calling store was compiled into Pro or into Platform is immaterial to it. What
> matters is that the value arriving at the store has already been normalised, and the
> normalisation is Platform-side at ingress.
>
> **Compensating static proof for the unreachable endpoints.** Audited every **request-bound**
> `DateTimeOffset` across all of `src/Verbara.Platform.Api/Endpoints/` — both `*Request` record
> properties (body-bound) and `DateTimeOffset?` handler parameters (query-bound) — checking each
> identifier for a `.ToUtcInstant()` / `.ToUniversalTime()` call. **Zero un-normalised ingress
> sites remain**, in the license-gated files as much as the reachable ones. The three identifiers
> the first pass flagged (`ScheduledReportEndpoints.nextRunAt`, `ManagementImpersonationEndpoints
> .expiresAt`, `SupervisorEndpoints.ownerOfflineSince`) are all **local variables**, not ingress;
> `ScheduledReportEndpoints.cs:84,156` wraps an NCrontab `GetNextOccurrence` result, whose
> `Unspecified` kind is accepted with any offset. So `AnalyticsEndpoints` and `CampaignEndpoints`
> carry the identical `.ToUtcInstant()` call shape as the site proven live above — see the branch
> diff, which touches both.
>
> **Honest residual.** This is a static equivalence argument for those two endpoints, not an
> execution of them. Closing the gap needs a signed license and belongs to whatever verification
> runs on a licensed host; it is not a blocker on this change.

> **Note on the 403 first seen here.** The initial attempts returned
> `403 "Tenant header does not match authenticated principal."` regardless of `X-Tenant-Id`.
> Cause: `TenantResolutionMiddleware.ResolveFromSubdomainAsync` splits `Request.Host.Host` on the
> first `.`, so host `127.0.0.1` yields subdomain `127`, and the fallback at
> `TenantResolutionMiddleware.cs:126` returns `new TenantId("127")` — which then mismatches the
> principal's `tid` in `TenantBoundaryValidationMiddleware`. Subdomain resolution runs *before*
> the `X-Tenant-Id` header, so the header cannot override it. This is the
> **`fix-ip-host-tenant-resolution`** defect, already declared Out of Scope in this change's
> proposal; it is not a timezone regression. Worked around for these calls by sending
> `Host: localhost:5199` (no dot → the subdomain branch returns null → the header is honoured).

- [x] 6.5 `openspec validate --all --strict` exit 0.
- [x] 6.6 CI green on the PR, all 5 required checks.

> **6.6 result — PR #256, run `32484193801`: every check green.** The five required contexts:
> `Build + Unit Tests (Release)` 3m31s · `Live-DB Tests (Postgres)` 3m40s · `AOT Publish (Api)`
> 6m12s · `Coverage Ratchet` 5m7s · `Analyze (C#)` 16m30s (plus `CodeQL`, `Coverage Script Tests`,
> `Dependency Review`, `Docs-only gate`, `Invariant Gates`, `OpenSpec Validate` — all pass).
> `Auto-merge safe Dependabot PRs` reports `skipping`, which is its correct state on a non-bot PR.
>
> Two of those deserve calling out rather than being read as a row in a table:
> - **`Invariant Gates` passes** with the widened gate #11 — the scope change from path-based to
>   reader-content-based did not trip on the existing tree, which is what it must not do.
> - **`Live-DB Tests (Postgres)` passes at STEP level**, not merely as a `continue-on-error`
>   badge: `Live-DB tests — Storage.Postgres.Tests=success`,
>   `Live-DB tests — Identity.Redis.Tests=success` read from the Actions API. This run is also
>   what closes run 2/2 of `fix-testcontainers-tcp-readiness` 5.1 — it is an *unrelated* PR by
>   that change's definition, which is exactly what its graduation bar asked for.
>
> This entry was written after that run and pushed on top of it, so the run recorded here is the
> one that validated the code; the push re-runs CI against the added prose.
- [x] 6.7 Record the `timestamptz` wire-format change (`-05:00` → `+00:00` on non-UTC hosts; no-op on UTC containers) in `CHANGELOG.md` under `[Unreleased]`, and confirm with `Verbara.Platform.Web` that no view parses the offset suffix literally.

> **6.7 Web check result:** no view parses the offset suffix. A grep for literal-suffix handling
> (`slice(0,19)`, `split('+')`, `endsWith('Z')`, offset regexes) across `Verbara.Platform.Web/src`
> returns zero non-test hits; all 147 date sites go through `new Date(...)` or `date-fns@4`, both of
> which parse full ISO-8601 with any offset correctly. The wire-format change is invisible to the frontend.
