<!--
APPROVED Web CHANGELOG draft for the 2026-07-platform-web-flush release train.
Drafted + approved 2026-07-24 (via /xr:change follow-up). Web's CHANGELOG.md [Unreleased]
was EMPTY across 20 commits since v3.15.0-web; this is the pre-authored [3.16.0-web] section.

/xr:release §H step 1 (land release content via PR) should:
  1. promote this section into Verbara.Platform.Web/CHANGELOG.md (replace the empty [Unreleased]),
  2. set the date to the actual tag day (placeholder below: 2026-07-24),
  3. bump Verbara.Platform.Web/package.json  "version": 3.15.0 -> 3.16.0,
  4. open the Web release PR, merge, then run the §H tag-push handoff for v3.16.0-web.

NOTE: no user-facing changes in these 20 commits — cut as a minor per the operator's decision
(notable internal typed-client migration). The Analytics typed-client child
(openapi-typed-client-analytics, 0/10 tasks) stays OPEN and is NOT part of this release.
-->

## [3.16.0-web] - 2026-07-24

Maintenance + internal-quality release — **no user-facing feature or behavior changes.**
Advances the OpenAPI typed-client migration (Operations + Agent modules), clears the npm
audit gate (one HIGH), and stands up the ADR-0012 Ola-2 / ADR-0013 CI invariant-gate suite.

### Changed

- **OpenAPI typed-client migration — Operations + Agent modules (`openapi-typed-client-operations`
  #215, `openapi-typed-client-agent` #219; Platform/ADR-0035).** Two more per-module children of the
  swap-the-T trilogy land on the generated `src/core/api/generated/openapi.d.ts`: the 3 Operations
  REST hooks (`use-cluster.ts`, `use-queue-metrics.ts`, `use-supervisor.ts` — 14 hand-written
  declarations) and the 8 Agent-module hook files (22 declarations) now consume
  `components['schemas'][...]` behind `client.ts`'s generic `<T>`, so contract drift against Platform
  is caught at `tsc -b` instead of at runtime (the csat-runner / v3.13.1-web failure class).
  Compile-time only — no runtime behavior change; `number | string` AOT wire-unions normalized at the
  hook boundary. The 3 Operations `*-state-stream.ts` hub-event hooks stay hand-written (SignalR
  payloads have no REST path — ADR-0020 deferred follow-up, owner Pro). The **Analytics** child
  (`openapi-typed-client-analytics`) remains open and ships in a later release.
- **Domain isolation + component-size caps (ADR-0012 Ola 2, #210).** Removes 14 cross-domain imports
  that had let the admin / agent / analytics / operations modules reach into each other: 4 shared UI
  primitives relocated to their true home `@/core/ui` and `agent-ai-store` to `@/core/stores` (~90
  importers rewritten — relocation, not an eslint-disable allowlist). Compile-time refactor; no
  runtime behavior change.

### Security

- **`fast-uri` HIGH advisory remediated (#216).** `npm audit fix` bumps transitive `fast-uri`
  3.1.3 → 3.1.4 (lockfile-only, non-breaking), clearing the host-confusion HIGH
  (GHSA-v2hh-gcrm-f6hx / GHSA-4c8g-83qw-93j6) that had turned the blocking
  `npm audit --audit-level=high` CI gate red repo-wide.
- **Four npm audit advisories cleared (#212).** `js-yaml` HIGH (GHSA-52cp-r559-cp3m, YAML merge-key
  quadratic CPU — forced to patched 4.x via an `overrides` past redocly's exact pin),
  `brace-expansion` HIGH (GHSA-3jxr-9vmj-r5cp, DoS), and `body-parser` LOW, all in dev/tooling-
  transitive deps. `npm audit` now reports 0 vulnerabilities; no runtime dependency touched.

### Added

- **CI invariant-gate suite — ADR-0012 Ola 2 + ADR-0013 (#199, #209, #210).** New PR-blocking gates,
  all wired into existing required jobs (no ruleset change): **coverage gate v2** (patch-coverage +
  two-sided band + exclusion baseline, byte-identical to the Sdk reference; verbara-meta/ADR-0013),
  **bundle-size budget** (`size-limit`, brotli app-JS frozen at 1.45 MB, ratchet-down; Gate #10),
  **generated-types adoption ratchet** (every `src/core/api/hooks` file must import the generated
  module; 45-hook frozen shrinking baseline; Gate #5), **domain-isolation** `no-restricted-imports`
  (Gate #4) and a **`max-lines: 1250`** component cap (Gate #9b).

### Fixed

- **Patch-coverage liveness self-test over-fired on non-executable diffs (#213, ADR-0013).** The
  liveness guard now trips only when a diff adds a plausibly-executable line in a non-test file under
  a coverage-instrumented root — so import-path refactors, pure renames, comment/type-only edits, and
  config-only changes correctly measure 0 and pass as n/a, while a genuinely mis-wired report still
  fails loud. Byte-identical to the Sdk/Pro/Platform coverage-gate-v2 parity roll.

### Housekeeping

- **Architecture charter docs (#214)** — `architecture.md` + `gates.yaml` (ADR-0014 §1 charter + §2
  gate manifest).
- **Dependabot CI-load reduction (#200)** (R-010) and routine dependency bumps — runtime:
  `wavesurfer.js` 7.12.11, `libphonenumber-js` 1.13.9; dev/tooling: `prettier` 3.9.5, `@types/node`,
  `@sentry/vite-plugin`, `allure-playwright`, `@axe-core/playwright`, `actions/setup-python`.
- **OpenSpec housekeeping** — archived `openapi-typed-client-operations` (#218) and
  `openapi-typed-client-agent` (#220).
