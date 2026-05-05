# E2E Testing with Playwright — Design Spec

> **Date:** 2026-03-31
> **Scope:** Sprint 1 — Platform Admin (host tenant) + Login
> **Location:** `Verbara.Platform.Web/tests/e2e/`
> **Approach:** Playwright in the frontend repo, tests against running demo environment

## Overview

End-to-end testing suite using Playwright to validate the Asterisk Platform frontend. Sprint 1 covers login flows and the full Platform Admin area (host tenant). Future sprints extend coverage to Tenant Administration and Agent Workspace.

### Why Playwright

- Native browser automation (Chromium, Firefox, WebKit)
- TypeScript-first, aligns with the React 19 + TS 5.9 frontend stack
- Built-in `storageState` for session reuse across tests
- `request` context for API-level setup/teardown without UI overhead
- Parallel test execution with worker isolation

### Precondition

The demo environment must be running (`docker/demo/demo-reset.sh`). Playwright does NOT orchestrate Docker — it tests against the already-running stack at `http://localhost` (web) and `http://localhost:5000` (API).

---

## Project Structure

```
Verbara.Platform.Web/
├── tests/
│   └── e2e/
│       ├── playwright.config.ts          # Base config: baseURL, projects, timeouts
│       ├── fixtures/
│       │   ├── auth.fixture.ts           # Login via API, storageState per role
│       │   └── api.fixture.ts            # Direct API helpers (seed, cleanup, assertions)
│       ├── helpers/
│       │   ├── credentials.ts            # Demo credentials centralized
│       │   └── selectors.ts              # Reusable data-testid selectors
│       └── tests/
│           └── platform-admin/           # Sprint 1
│               ├── login.spec.ts
│               ├── system-settings.spec.ts
│               ├── diagnostics.spec.ts
│               ├── tenant-management.spec.ts
│               ├── auth-config.spec.ts
│               ├── auth-events.spec.ts
│               ├── auth-sessions.spec.ts
│               ├── audit.spec.ts
│               ├── security.spec.ts
│               └── setup-wizard.spec.ts
├── package.json                          # Add @playwright/test + scripts
└── ...
```

### npm Scripts

```json
{
  "e2e": "playwright test -c tests/e2e/playwright.config.ts",
  "e2e:ui": "playwright test -c tests/e2e/playwright.config.ts --ui",
  "e2e:headed": "playwright test -c tests/e2e/playwright.config.ts --headed",
  "e2e:debug": "playwright test -c tests/e2e/playwright.config.ts --debug"
}
```

---

## Playwright Configuration

```typescript
// tests/e2e/playwright.config.ts
{
  testDir: './tests',
  timeout: 30_000,
  retries: 1,
  workers: 1,                // Sequential for Sprint 1 (shared state)
  use: {
    baseURL: 'http://localhost',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
}
```

**Sprint 1 uses `workers: 1`** because tests share demo state (creating/deleting tenants affects other tests). Future sprints can parallelize with isolated test data.

---

## Auth Fixture Strategy

### `auth.fixture.ts`

Extends Playwright's `test` with a `platformAdmin` and `demoAdmin` fixture that:

1. Calls `POST /api/auth/login` via `request` context (no browser UI)
2. Stores the JWT + cookies as `storageState` in a temp file
3. Injects the authenticated state into the browser context

```typescript
// Conceptual shape
type AuthFixtures = {
  platformAdminPage: Page;   // Logged in as platform@admin.local
  demoAdminPage: Page;       // Logged in as admin@demo.local
  apiContext: APIRequestContext; // For direct API calls (seed/cleanup)
};
```

**Login tests (`login.spec.ts`)** do NOT use the fixture — they test the login UI directly.

All other Sprint 1 tests use `platformAdminPage` to avoid repeating login through the UI. The fixture sets `localStorage` with `tenantId: "platform"` so the frontend sends `X-Tenant-Id: platform` on all API calls (matching the auth store behavior after a real login).

### `api.fixture.ts`

Provides helpers for test setup/teardown:

- `createTenant(data)` / `deleteTenant(id)` — for tests that need fresh tenants
- `createUser(tenantId, data)` / `deleteUser(tenantId, id)` — for user tests
- `getAuthEvents(params)` — for verifying audit entries after actions
- `resetTestData()` — cleanup between test runs

---

## Credentials (from demo-reset.sh)

```typescript
// helpers/credentials.ts
export const PLATFORM_ADMIN = {
  tenantId: 'platform',
  email: 'platform@admin.local',
  password: 'PlatformAdmin2026!',
};

export const DEMO_ADMIN = {
  tenantId: 'demo',
  email: 'admin@demo.local',
  password: 'DemoAdmin2026!',
};

export const API_BASE = 'http://localhost:5000';
export const WEB_BASE = 'http://localhost';
```

---

## Selector Strategy

**Primary:** `data-testid` attributes added to frontend components.
**Fallback:** Accessible selectors (`getByRole`, `getByLabel`, `getByText`).
**Avoid:** CSS classes, DOM structure, implementation-specific selectors.

### data-testid Additions Required

The frontend currently has **NO `data-testid` attributes**. These must be added as part of the implementation plan:

**Login page:**
- `data-testid="login-email"`, `login-password"`, `login-submit"`, `login-error"`
- `data-testid="login-apikey-toggle"`, `login-apikey-input"`, `login-apikey-submit"`
- `data-testid="login-sso-button"`, `login-forgot-password"`

**Admin sidebar:**
- `data-testid="sidebar-{section}"` for each menu group
- `data-testid="sidebar-item-{name}"` for each menu item

**Shared components:**
- `data-testid="page-header"`, `page-header-title"`, `page-header-action"`
- `data-testid="data-table"`, `data-table-search"`, `data-table-row-{id}"`
- `data-testid="confirm-dialog"`, `confirm-dialog-confirm"`, `confirm-dialog-cancel"`
- `data-testid="sheet-form"`, `sheet-form-submit"`, `sheet-form-cancel"`
- `data-testid="toast-success"`, `toast-error"`

**System page:**
- `data-testid="license-card"`, `cluster-card"`, `settings-form"`
- `data-testid="settings-platform-name"`, `settings-timezone"`, `settings-language"`, `settings-save"`
- `data-testid="node-card-{id}"`, `node-drain-button-{id}"`

**Tenants page:**
- `data-testid="tenant-create-button"`, `tenant-table"`
- `data-testid="tenant-form-id"`, `tenant-form-name"`, `tenant-form-channels"`, `tenant-form-campaigns"`
- `data-testid="tenant-edit-{id}"`, `tenant-delete-{id}"`
- `data-testid="tenant-status-{id}"`

**Auth config page:**
- `data-testid="mfa-policy-{value}"` (optional, required_for_roles, required_all)
- `data-testid="password-min-length"`, `password-uppercase"`, `password-number"`, `password-special"`
- `data-testid="lockout-threshold"`, `lockout-duration"`
- `data-testid="session-idle-timeout"`, `session-absolute-timeout"`
- `data-testid="oidc-toggle"`, `oidc-authority"`, `oidc-client-id"`, `oidc-client-secret"`
- `data-testid="auth-config-save"`

**Auth events page:**
- `data-testid="events-filter-type"`, `events-filter-user"`, `events-filter-start"`, `events-filter-end"`
- `data-testid="events-search-button"`, `events-export-button"`, `events-table"`

**Auth sessions page:**
- `data-testid="sessions-table"`, `session-force-logout-{id}"`, `force-logout-confirm"`

**Audit page:**
- `data-testid="audit-filter-action"`, `audit-filter-entity"`, `audit-filter-user"`
- `data-testid="audit-filter-from"`, `audit-filter-to"`, `audit-search-button"`, `audit-table"`

**Diagnostics page:**
- `data-testid="diag-platform-card"`, `diag-license-card"`, `diag-cluster-card"`
- `data-testid="diag-nodes-table"`

**Security page:**
- `data-testid="mfa-status"`, `mfa-setup-button"`, `mfa-qr-code"`, `mfa-verify-code"`
- `data-testid="mfa-recovery-codes"`, `mfa-disable-button"`
- `data-testid="password-current"`, `password-new"`, `password-confirm"`, `password-change-button"`

**Setup wizard:**
- `data-testid="setup-banner"`, `setup-banner-resume"`, `setup-banner-dismiss"`
- `data-testid="setup-step-{n}"`, `setup-next"`, `setup-back"`, `setup-skip"`

---

## Test Cases — Sprint 1 (62 tests)

### 1. `login.spec.ts` — Authentication (8 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Login exitoso platform admin | Navigate `/login`, fill email+password, submit | Redirect to admin, display name in header |
| 2 | Login exitoso demo admin | Fill demo credentials with tenant | Redirect, tenant "demo" active |
| 3 | Login fallido — password incorrecto | Wrong password, submit | Error message visible, stays on `/login` |
| 4 | Login fallido — email inexistente | Nonexistent email, submit | Generic error (no user enumeration) |
| 5 | Login fallido — campos vacíos | Submit empty form | Form validation errors visible |
| 6 | Logout | Login → click logout button | Redirect to `/login`, `/admin/*` inaccessible |
| 7 | Ruta protegida sin sesión | Navigate directly to `/admin/tenants` without login | Redirect to `/login` |
| 8 | Sesión persiste tras recargar | Login → reload page | Still authenticated, no re-login required |

### 2. `system-settings.spec.ts` — System + Cluster (7 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver license card | Navigate to system page | Tier, maxAgents, features visible |
| 2 | Ver cluster status | Same page, cluster section | instanceId, totalChannels, totalAgents visible |
| 3 | Ver nodos del cluster | Nodes section | At least 1 node "online" with capacity |
| 4 | Ver global settings | Settings form section | platformName, timezone, language visible |
| 5 | Editar settings | Change platformName → save → reload | Value persists |
| 6 | Save disabled sin cambios | No modifications | Save button disabled |
| 7 | Drain button state | Check drain button on healthy node | Disabled or enabled based on node state |

### 3. `diagnostics.spec.ts` — Health Check (4 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Platform card | Navigate to diagnostics | Version, tenant, setup status visible |
| 2 | License card | Same page | Tier badge, max agents, feature badges |
| 3 | Cluster nodes table | Same page | Columns: nodeId, state, weight, capacity, version |
| 4 | Auto-refresh | Wait 15s | Data refreshes (verify timestamp or network request) |

### 4. `tenant-management.spec.ts` — CRUD Tenants (10 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver lista de tenants | Navigate to tenants page | Table with "platform" and "demo" |
| 2 | Columnas correctas | Inspect table | tenantId, name, status badge, maxChannels, maxCampaigns |
| 3 | Buscar tenant | Type "demo" in search | Only matching tenant shown |
| 4 | Crear tenant | Open sheet → fill form → submit | New tenant in list, success toast |
| 5 | Validación tenantId | Enter "INVALID!" in tenantId field | Validation error (regex: `^[a-z0-9-]+$`) |
| 6 | Editar tenant | Open edit → change name + limits → save | Updated values in list |
| 7 | Suspender tenant | Edit status to "suspended" | Status badge changes |
| 8 | Reactivar tenant | Edit status back to "active" | Status badge returns to active |
| 9 | Eliminar tenant | Click delete → confirm (3s delay) → confirm | Tenant removed from list |
| 10 | No eliminar tenant con hijos | Try to delete "platform" | Error, tenant persists |

### 5. `auth-config.spec.ts` — Auth Policies (8 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver MFA policy | Navigate to auth config | Radio buttons visible with current selection |
| 2 | Cambiar MFA policy | Select "required_all" → save | Persists after reload |
| 3 | Ver password policy | Scroll to password section | Min length, toggles visible with current values |
| 4 | Editar password policy | Change min length → save | Persists after reload |
| 5 | Ver lockout policy | Scroll to lockout section | Threshold + duration visible |
| 6 | Ver session timeouts | Scroll to sessions section | Idle + absolute timeout visible |
| 7 | Toggle OIDC/SSO | Enable toggle | Authority, clientId, secret fields appear |
| 8 | Save disabled sin cambios | No modifications made | Save button disabled |

### 6. `auth-events.spec.ts` — Security Log (6 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver eventos | Navigate to auth events | Table with login events from setup |
| 2 | Columnas correctas | Inspect table | Timestamp, user, event type badge, IP, details |
| 3 | Filtrar por event type | Select "login_success" | Only login_success events shown |
| 4 | Filtrar por date range | Set from/to dates | Filtered results |
| 5 | Paginación | Navigate pages if >50 events | Prev/next, "X-Y of Z" text |
| 6 | Export CSV | Click export button | File downloads as .csv |

### 7. `auth-sessions.spec.ts` — Active Sessions (5 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver sesiones activas | Navigate to sessions page | At least 1 session (current test session) |
| 2 | Columnas correctas | Inspect table | User, IP, browser, started, last activity |
| 3 | Force logout otra sesión | Create 2nd session via API → force logout from UI | Session disappears from list |
| 4 | Auto-refresh | Wait 30s | Network request fires, data refreshes |
| 5 | Confirm dialog on force logout | Click force logout | Destructive confirm dialog appears |

### 8. `audit.spec.ts` — Admin Trail (5 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver audit log | Navigate to audit page | Events from demo setup (create tenant, etc.) |
| 2 | Columnas correctas | Inspect table | Timestamp, action badge, entity type, entity ID, performed by |
| 3 | Filtrar por action | Enter "create" | Only create events |
| 4 | Filtrar por entity type | Enter "User" | Only user events |
| 5 | Paginación | Check pagination controls | 25 items/page, prev/next buttons |

### 9. `security.spec.ts` — Personal Security (6 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Ver estado MFA | Navigate to security page | "Disabled" badge visible |
| 2 | Setup MFA completo | Click setup → QR visible → enter TOTP code | Recovery codes displayed, MFA enabled |
| 3 | Recovery codes descargables | After MFA setup | Copy + Download buttons functional |
| 4 | Disable MFA | Click disable → enter password → confirm | Badge returns to "Disabled" |
| 5 | Cambiar password | Fill old + new + confirm → submit | Success message |
| 6 | Password validation | New ≠ confirm → submit | Validation error visible |

**Note:** Test 2 (MFA setup) requires generating a valid TOTP code. The fixture will use the `otpauth://` URI returned by `/api/auth/mfa/setup` with a TOTP library (`otpauth` npm package) to generate the 6-digit code programmatically.

### 10. `setup-wizard.spec.ts` — First Boot (3 tests)

| # | Test | Action | Assertion |
|---|------|--------|-----------|
| 1 | Setup blocked if initialized | POST /api/setup via API | Error response (platform already exists) |
| 2 | Setup banner dismissible | If banner visible → click dismiss | Banner disappears |
| 3 | Wizard navigation | Open wizard → Back/Next/Skip buttons | Navigation between steps works |

---

## Test Data Strategy

### Demo State Assumptions

Tests assume `demo-reset.sh` has run successfully:
- Host tenant "platform" exists with platform admin
- Customer tenant "demo" exists with demo admin + 6 agents
- RBAC seeded with 60 permissions + 8 role templates

### Test Isolation

- **Non-destructive tests** (view, filter, search): Run against existing demo data
- **Destructive tests** (create, edit, delete tenant): Use unique IDs prefixed with `e2e-` (e.g., `e2e-test-tenant-{timestamp}`) and clean up in `afterEach`/`afterAll`
- **Config tests** (auth config, system settings): Save original values in `beforeAll`, restore in `afterAll`
- **MFA tests**: Setup and teardown MFA in the same test to leave state clean

### TOTP Generation

For MFA tests, add `otpauth` npm package (dev dependency) to generate valid TOTP codes from the secret returned by the API.

---

## data-testid Convention

Format: `{page}-{component}-{identifier}`

Examples:
- `login-email-input`, `login-submit-button`
- `tenants-table`, `tenants-create-button`, `tenants-row-demo`
- `system-license-card`, `system-settings-save`

Where identifiers are dynamic: `{component}-{dynamicId}` (e.g., `tenants-row-demo`, `node-card-node1`)

---

## Roadmap — Full E2E Coverage

### Sprint 1: Platform Admin (this spec) — 10 files, 62 tests
- Login/logout + session management
- System settings + diagnostics + cluster
- Tenant CRUD
- Auth config + events + sessions
- Audit trail
- Personal security (MFA, password)
- Setup wizard

### Sprint 2: Tenant Administration — ~15 files, ~100 tests
- User CRUD + role assignment
- Agent CRUD + skill assignment + SIP config
- Queue CRUD + member management
- Channel configuration + activation
- Role management (create, clone, edit permissions, delete)
- Skill management
- Team management
- Realtime settings
- RBAC permission enforcement (admin vs supervisor visibility)

### Sprint 3: Operations & Analytics — ~8 files, ~50 tests
- Wallboard (live queue metrics)
- Supervisor monitor (conversation list)
- Agent states (real-time table)
- Campaign monitor
- Analytics dashboard (charts, KPIs)
- CDR viewer + filtering + export
- Interval snapshots
- QA evaluations

### Sprint 4: Agent Workspace — ~6 files, ~40 tests
- Inbox panel (conversation list)
- Conversation view (message thread)
- Reply composer (send messages, canned responses)
- Context panel (contact info, history, notes, knowledge base)
- Agent state transitions
- Transfer/hold flows

### Sprint 5: Advanced Flows — ~8 files, ~50 tests
- Campaign wizard (6-step creation)
- Flow designer (XY Flow canvas, node palette, connections)
- DNC list management + bulk import
- Caller ID pool management
- Holiday calendar management
- Trunk configuration
- Outbound route management + drag-to-reorder
- Bot configuration + KB management

### Sprint 6: Cross-Cutting — ~4 files, ~30 tests
- Multi-browser (Firefox, WebKit)
- Responsive/mobile viewport tests
- Visual regression (screenshot comparison)
- Performance (page load times, LCP)
- Accessibility (axe-core integration)
- API key authentication flow
- OIDC/SSO flow (mock IdP)
- Cross-tenant operations (platform admin managing demo tenant)

**Total roadmap: ~51 files, ~330+ tests across 6 sprints.**

---

## Dependencies to Add

```json
// package.json devDependencies
{
  "@playwright/test": "^1.52.0",
  "otpauth": "^9.4.0"
}
```

Post-install: `npx playwright install chromium`

---

## CI/CD Integration (Future)

```yaml
# Conceptual — not implemented in Sprint 1
e2e:
  needs: [demo-up]
  steps:
    - demo-reset.sh       # Start demo environment
    - npm run e2e          # Run Playwright
    - Upload artifacts     # Screenshots, videos, traces on failure
```

Sprint 1 runs locally. CI pipeline added in Sprint 2 once patterns are stable.
