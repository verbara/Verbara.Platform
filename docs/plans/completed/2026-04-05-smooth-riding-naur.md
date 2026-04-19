# Plan: Stabilization Sprint — Platform v1.3.1

## Context

Platform v1.3.1 is functionally complete (1,103 backend tests passing, 0 warnings). However, several housekeeping items need attention before moving to v1.4.0:
- vitest.config.ts runs E2E specs through Vitest (wrong runner), causing `npm run test` failures
- CLAUDE.md files are stale (wrong test counts, missing v1.3.1 changes)
- Demo script uses deprecated `/api/` paths (works via redirect but logs warnings)
- E2E coverage: 0% on Operations, Analytics, Agent Workspace

## Deliverables

### 1. Fix vitest.config.ts E2E exclusion (Platform.Web)
**File:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/vitest.config.ts`

Add `exclude: ['**/tests/e2e/**', '**/node_modules/**']` to the `test` block. This makes `npm run test` run only the 4 unit test files (28 tests) without attempting Playwright specs.

### 2. Update demo-reset.sh to versioned paths
**File:** `/media/Data/Source/IPcom/Asterisk.Platform/docker/demo/demo-reset.sh`

Replace all 10 `/api/` calls with `/api/v1/` equivalents. The `/health` endpoint stays unversioned (it's not under `/api/`).

### 3. Update docs/demo-environment.md
**File:** `/media/Data/Source/IPcom/Asterisk.Platform/docs/demo-environment.md`

Add API versioning note to the "What Works" section.

### 4. Update Platform CLAUDE.md
**File:** `/media/Data/Source/IPcom/Asterisk.Platform/CLAUDE.md`

- Update test count from "1,259" to "1,103" (actual count from test run)
- Add v1.3.1 Web sync section (API URL migration, GDPR fix)
- Bump version references

### 5. Update Platform.Web CLAUDE.md
**File:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/CLAUDE.md`

- Update test count from "202" to "241" actual E2E tests
- Update version to v1.3.1
- Add note about vitest exclude fix
- Update API layer section to mention `/api/v1/` prefix

### 6. E2E Sprint 3: Operations & Analytics (~50 tests)
**Location:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/tests/e2e/tests/`

New spec files covering the 0% coverage areas:

#### Operations (4 specs, ~20 tests)
- `operations/wallboard.spec.ts` — Queue cards, real-time display, refresh
- `operations/agent-states.spec.ts` — Agent list, state changes, search
- `operations/campaign-monitor.spec.ts` — Campaign list, status, metrics
- `operations/monitor.spec.ts` — Active sessions, whisper, listen

#### Analytics (5 specs, ~25 tests)
- `analytics/dashboard.spec.ts` — KPI cards, chart render, date range
- `analytics/cdr.spec.ts` — CDR table, detail drawer, filters
- `analytics/qa.spec.ts` — QA table, detail drawer, scoring
- `analytics/intervals.spec.ts` — Interval table, queue filter
- `analytics/agent-intervals.spec.ts` — Agent interval data

#### Admin gaps (2 specs, ~8 tests)
- `platform-admin/cluster.spec.ts` — Node CRUD, drain, instances
- `platform-admin/agent-assist.spec.ts` — Config, keyword rules, compliance

### 7. Update memory
Update MEMORY.md with final state after all deliverables.

## Execution Order

1. **Batch A (quick fixes):** vitest config, demo-reset.sh, demo docs — 3 parallel subagents
2. **Batch B (docs):** Both CLAUDE.md updates — 2 parallel subagents
3. **Batch C (E2E):** Sprint 3 specs — parallel subagents by area (Operations, Analytics, Admin gaps)
4. **Batch D:** Verification run + memory update

## Verification

```bash
# Platform backend
dotnet test Asterisk.Platform.slnx -v q  # 1,103 pass, 0 fail

# Platform.Web unit tests
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
npm run test  # 28 pass (4 unit test files), 0 fail, no E2E contamination

# Platform.Web build
npm run build  # 0 errors

# E2E (requires running demo)
npx playwright test  # ~290 total tests
```
