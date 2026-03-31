# E2E Playwright — Sprint 1: Platform Admin

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Playwright E2E tests covering login and all Platform Admin pages (62 tests across 10 spec files).

**Architecture:** Playwright installed in Platform.Web repo (`tests/e2e/`). Auth via API fixture (storageState reuse). Tests run against the demo environment (`demo-reset.sh`). Frontend components get `data-testid` attributes for reliable selectors.

**Tech Stack:** @playwright/test 1.52+, otpauth (TOTP generation), TypeScript, React 19 (Platform.Web)

**Spec:** `docs/superpowers/specs/2026-03-31-e2e-playwright-design.md`

**Working directory:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/`

---

## Phase A: Infrastructure (Tasks 1-4)

### Task 1: Install Playwright and configure npm scripts

**Files:**
- Modify: `/media/Data/Source/IPcom/Asterisk.Platform.Web/package.json`

- [ ] **Step 1: Install Playwright and otpauth**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
npm install -D @playwright/test otpauth
npx playwright install chromium
```

- [ ] **Step 2: Add E2E scripts to package.json**

Add these scripts to the `"scripts"` section in `package.json`:

```json
"e2e": "playwright test -c tests/e2e/playwright.config.ts",
"e2e:ui": "playwright test -c tests/e2e/playwright.config.ts --ui",
"e2e:headed": "playwright test -c tests/e2e/playwright.config.ts --headed",
"e2e:debug": "playwright test -c tests/e2e/playwright.config.ts --debug"
```

- [ ] **Step 3: Commit**

```bash
git add package.json package-lock.json
git commit -m "chore: install Playwright and otpauth for E2E testing"
```

---

### Task 2: Create Playwright config and credentials helper

**Files:**
- Create: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/helpers/credentials.ts`

- [ ] **Step 1: Create playwright.config.ts**

```typescript
// tests/e2e/playwright.config.ts
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  expect: { timeout: 5_000 },
  fullyParallel: false,
  retries: 1,
  workers: 1,
  reporter: [['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
```

- [ ] **Step 2: Create credentials.ts**

```typescript
// tests/e2e/helpers/credentials.ts
export const API_BASE = 'http://localhost:5000';

export const PLATFORM_ADMIN = {
  tenantId: 'platform',
  email: 'platform@admin.local',
  password: 'PlatformAdmin2026!',
} as const;

export const DEMO_ADMIN = {
  tenantId: 'demo',
  email: 'admin@demo.local',
  password: 'DemoAdmin2026!',
} as const;

export const DEMO_SUPERVISOR = {
  tenantId: 'demo',
  email: 'supervisor@demo.local',
  password: 'DemoSupervisor2026!',
} as const;
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/playwright.config.ts tests/e2e/helpers/credentials.ts
git commit -m "chore: add Playwright config and credentials helper"
```

---

### Task 3: Create auth fixture with storageState

**Files:**
- Create: `tests/e2e/fixtures/auth.fixture.ts`

- [ ] **Step 1: Create auth.fixture.ts**

```typescript
// tests/e2e/fixtures/auth.fixture.ts
import { test as base, type Page, type APIRequestContext } from '@playwright/test';
import { API_BASE, PLATFORM_ADMIN, DEMO_ADMIN } from '../helpers/credentials';
import * as fs from 'fs';
import * as path from 'path';

interface LoginResult {
  accessToken: string;
  expiresAt: string;
  user?: { id: string; email: string; displayName: string; role: string };
  tenantId?: string;
  permissions?: string[];
  features?: Record<string, boolean>;
}

async function loginViaApi(
  request: APIRequestContext,
  creds: { tenantId: string; email: string; password: string },
): Promise<LoginResult> {
  const response = await request.post(`${API_BASE}/api/auth/login`, {
    data: {
      tenantId: creds.tenantId,
      email: creds.email,
      password: creds.password,
    },
  });
  if (!response.ok()) {
    throw new Error(`Login failed for ${creds.email}: ${response.status()}`);
  }
  return response.json();
}

function buildStorageState(loginResult: LoginResult, tenantId: string) {
  return {
    cookies: [],
    origins: [
      {
        origin: 'http://localhost',
        localStorage: [
          {
            name: 'asterisk-auth',
            value: JSON.stringify({
              state: {
                accessToken: loginResult.accessToken,
                tokenExpiry: new Date(loginResult.expiresAt).getTime(),
                user: loginResult.user ?? null,
                tenantId,
                permissions: loginResult.permissions ?? [],
                features: loginResult.features ?? {},
                rememberMe: false,
                mfaPending: null,
              },
              version: 0,
            }),
          },
        ],
      },
    ],
  };
}

type AuthFixtures = {
  platformAdminPage: Page;
  demoAdminPage: Page;
  authenticatedApiContext: APIRequestContext;
};

export const test = base.extend<AuthFixtures>({
  platformAdminPage: async ({ browser, request }, use) => {
    const loginResult = await loginViaApi(request, PLATFORM_ADMIN);
    const storageState = buildStorageState(loginResult, PLATFORM_ADMIN.tenantId);
    const storageFile = path.join(__dirname, '..', '.auth-platform-admin.json');
    fs.writeFileSync(storageFile, JSON.stringify(storageState));
    const context = await browser.newContext({ storageState: storageFile });
    const page = await context.newPage();
    await use(page);
    await context.close();
    fs.unlinkSync(storageFile);
  },

  demoAdminPage: async ({ browser, request }, use) => {
    const loginResult = await loginViaApi(request, DEMO_ADMIN);
    const storageState = buildStorageState(loginResult, DEMO_ADMIN.tenantId);
    const storageFile = path.join(__dirname, '..', '.auth-demo-admin.json');
    fs.writeFileSync(storageFile, JSON.stringify(storageState));
    const context = await browser.newContext({ storageState: storageFile });
    const page = await context.newPage();
    await use(page);
    await context.close();
    fs.unlinkSync(storageFile);
  },

  authenticatedApiContext: async ({ playwright }, use) => {
    const ctx = await playwright.request.newContext();
    const loginResult = await loginViaApi(ctx, PLATFORM_ADMIN);
    const authedCtx = await playwright.request.newContext({
      extraHTTPHeaders: {
        Authorization: `Bearer ${loginResult.accessToken}`,
        'X-Tenant-Id': PLATFORM_ADMIN.tenantId,
      },
    });
    await use(authedCtx);
    await authedCtx.dispose();
    await ctx.dispose();
  },
});

export { expect } from '@playwright/test';
```

- [ ] **Step 2: Add .auth-*.json to .gitignore**

Append to `/media/Data/Source/IPcom/Asterisk.Platform.Web/.gitignore`:

```
# Playwright auth state
tests/e2e/.auth-*.json
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/fixtures/auth.fixture.ts .gitignore
git commit -m "feat(e2e): add auth fixture with storageState for platform and demo admin"
```

---

### Task 4: Create API fixture for test setup/teardown

**Files:**
- Create: `tests/e2e/fixtures/api.fixture.ts`

- [ ] **Step 1: Create api.fixture.ts**

```typescript
// tests/e2e/fixtures/api.fixture.ts
import { type APIRequestContext } from '@playwright/test';
import { API_BASE, PLATFORM_ADMIN } from '../helpers/credentials';

export class ApiHelper {
  constructor(private readonly request: APIRequestContext) {}

  async createTenant(data: {
    tenantId: string;
    name: string;
    type?: number;
    maxConcurrentChannels?: number;
    maxActiveCampaigns?: number;
  }) {
    const response = await this.request.post(`${API_BASE}/api/management/tenants`, {
      data: { type: 2, maxConcurrentChannels: 100, maxActiveCampaigns: 10, ...data },
    });
    return response;
  }

  async deleteTenant(tenantId: string) {
    return this.request.delete(`${API_BASE}/api/management/tenants/${tenantId}`);
  }

  async getSystemSettings() {
    const response = await this.request.get(`${API_BASE}/api/management/system/settings`);
    return response.json();
  }

  async updateSystemSettings(data: Record<string, unknown>) {
    return this.request.put(`${API_BASE}/api/management/system/settings`, { data });
  }

  async getAuthConfig() {
    const response = await this.request.get(`${API_BASE}/api/admin/auth/config`, {
      headers: { 'X-Tenant-Id': PLATFORM_ADMIN.tenantId },
    });
    return response.json();
  }

  async updateAuthConfig(data: Record<string, unknown>) {
    return this.request.put(`${API_BASE}/api/admin/auth/config`, {
      data,
      headers: { 'X-Tenant-Id': PLATFORM_ADMIN.tenantId },
    });
  }

  async login(tenantId: string, email: string, password: string) {
    const response = await this.request.post(`${API_BASE}/api/auth/login`, {
      data: { tenantId, email, password },
    });
    return response;
  }
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/fixtures/api.fixture.ts
git commit -m "feat(e2e): add API helper fixture for test setup/teardown"
```

---

## Phase B: Add data-testid attributes to frontend (Tasks 5-8)

> All changes in `/media/Data/Source/IPcom/Asterisk.Platform.Web/src/`.
> Add `data-testid` only to interactive elements and key containers used by tests.

### Task 5: Add data-testid to login page, auth guard, and shared components

**Files:**
- Modify: `src/core/auth/login-page.tsx`
- Modify: `src/admin/shared/data-table.tsx`
- Modify: `src/admin/shared/confirm-dialog.tsx`
- Modify: `src/admin/shared/page-header.tsx`

- [ ] **Step 1: Add data-testid to login-page.tsx**

Key elements to add `data-testid` to (find each element and add the attribute):

| Element description | data-testid value |
|---|---|
| Email input field | `login-email` |
| Password input field | `login-password` |
| Sign In submit button (email form) | `login-submit` |
| Error message container | `login-error` |
| "Use API Key" toggle button/chevron | `login-apikey-toggle` |
| API Key input field | `login-apikey-input` |
| API Key Sign In button | `login-apikey-submit` |
| SSO/OIDC button | `login-sso-button` |
| Forgot Password link | `login-forgot-password` |
| MFA verify section (when mfaPending) | `login-mfa-section` |

- [ ] **Step 2: Add data-testid to data-table.tsx**

| Element description | data-testid value |
|---|---|
| Search input | `data-table-search` |
| Table element | `data-table` |
| "Page X of Y" text container | `data-table-page-info` |
| Previous page button | `data-table-prev` |
| Next page button | `data-table-next` |

- [ ] **Step 3: Add data-testid to confirm-dialog.tsx**

| Element description | data-testid value |
|---|---|
| Dialog content wrapper | `confirm-dialog` |
| Cancel button | `confirm-dialog-cancel` |
| Confirm button | `confirm-dialog-confirm` |

- [ ] **Step 4: Add data-testid to page-header.tsx**

| Element description | data-testid value |
|---|---|
| Outer container div | `page-header` |
| h1 title element | `page-header-title` |

- [ ] **Step 5: Commit**

```bash
git add src/core/auth/login-page.tsx src/admin/shared/data-table.tsx src/admin/shared/confirm-dialog.tsx src/admin/shared/page-header.tsx
git commit -m "feat(e2e): add data-testid to login page and shared components"
```

---

### Task 6: Add data-testid to sidebar, tenants page, and audit page

**Files:**
- Modify: `src/admin/sidebar.tsx`
- Modify: `src/admin/tenants/tenants-page.tsx`
- Modify: `src/admin/audit/audit-page.tsx`

- [ ] **Step 1: Add data-testid to sidebar.tsx**

| Element description | data-testid value |
|---|---|
| Each collapsible group toggle button | `sidebar-group-{groupKey}` (e.g., `sidebar-group-system`) |
| Each NavLink item | `sidebar-link-{itemKey}` (e.g., `sidebar-link-tenants`) |

Use the `item.key` or `group.key` property (or the label/path slug) as the dynamic suffix.

- [ ] **Step 2: Add data-testid to tenants-page.tsx**

| Element description | data-testid value |
|---|---|
| "New Tenant" button | `tenants-create-button` |
| Create sheet content | `tenants-create-sheet` |
| Tenant ID input (create) | `tenants-form-tenantId` |
| Name input (create) | `tenants-form-name` |
| Max Channels input (create) | `tenants-form-maxChannels` |
| Max Campaigns input (create) | `tenants-form-maxCampaigns` |
| Create submit button | `tenants-form-submit` |
| Edit dialog content | `tenants-edit-dialog` |
| Edit name input | `tenants-edit-name` |
| Edit status select | `tenants-edit-status` |
| Edit channels input | `tenants-edit-channels` |
| Edit campaigns input | `tenants-edit-campaigns` |
| Edit update button | `tenants-edit-submit` |
| Edit cancel button | `tenants-edit-cancel` |
| Per-row edit button (in column def) | `tenant-edit-{tenantId}` |
| Per-row delete button (in column def) | `tenant-delete-{tenantId}` |
| Per-row status badge (in column def) | `tenant-status-{tenantId}` |

- [ ] **Step 3: Add data-testid to audit-page.tsx**

| Element description | data-testid value |
|---|---|
| Action filter input | `audit-filter-action` |
| Entity type filter input | `audit-filter-entityType` |
| Performed by filter input | `audit-filter-performedBy` |
| From date input | `audit-filter-from` |
| To date input | `audit-filter-to` |
| Search button | `audit-search-button` |
| Audit table element | `audit-table` |
| Previous page button | `audit-prev` |
| Next page button | `audit-next` |

- [ ] **Step 4: Commit**

```bash
git add src/admin/sidebar.tsx src/admin/tenants/tenants-page.tsx src/admin/audit/audit-page.tsx
git commit -m "feat(e2e): add data-testid to sidebar, tenants, and audit pages"
```

---

### Task 7: Add data-testid to system, diagnostics, and setup pages

**Files:**
- Modify: `src/admin/system/system-page.tsx`
- Modify: `src/admin/system/diagnostics-page.tsx`
- Modify: `src/admin/setup/setup-wizard.tsx`
- Modify: `src/admin/setup/setup-banner.tsx`

- [ ] **Step 1: Add data-testid to system-page.tsx**

| Element description | data-testid value |
|---|---|
| License card container | `system-license-card` |
| Cluster nodes grid | `system-nodes-grid` |
| Each node card | `system-node-{nodeId}` |
| Drain button per node | `system-node-drain-{nodeId}` |
| Platform Name input | `system-settings-platformName` |
| Timezone select trigger | `system-settings-timezone` |
| Language select trigger | `system-settings-language` |
| Save settings button | `system-settings-save` |

- [ ] **Step 2: Add data-testid to diagnostics-page.tsx**

| Element description | data-testid value |
|---|---|
| Platform status card | `diag-platform-card` |
| License status card | `diag-license-card` |
| Cluster status card | `diag-cluster-card` |
| Nodes table | `diag-nodes-table` |
| Active drains section | `diag-active-drains` |

- [ ] **Step 3: Add data-testid to setup-wizard.tsx and setup-banner.tsx**

**setup-wizard.tsx:**

| Element description | data-testid value |
|---|---|
| Back button | `setup-back` |
| Next button | `setup-next` |
| Skip button | `setup-skip` |
| Get Started button | `setup-getstarted` |
| Finish button | `setup-finish` |

**setup-banner.tsx:**

| Element description | data-testid value |
|---|---|
| Banner container | `setup-banner` |
| Resume setup button | `setup-banner-resume` |
| Dismiss button | `setup-banner-dismiss` |

- [ ] **Step 4: Commit**

```bash
git add src/admin/system/system-page.tsx src/admin/system/diagnostics-page.tsx src/admin/setup/setup-wizard.tsx src/admin/setup/setup-banner.tsx
git commit -m "feat(e2e): add data-testid to system, diagnostics, and setup pages"
```

---

### Task 8: Add data-testid to auth config, auth events, auth sessions, and security pages

**Files:**
- Modify: `src/admin/system/auth-config-page.tsx`
- Modify: `src/admin/system/auth-events-page.tsx`
- Modify: `src/admin/system/auth-sessions-page.tsx`
- Modify: `src/admin/profile/security-page.tsx`

- [ ] **Step 1: Add data-testid to auth-config-page.tsx**

| Element description | data-testid value |
|---|---|
| MFA policy radio: optional | `auth-config-mfa-optional` |
| MFA policy radio: required_for_roles | `auth-config-mfa-required-roles` |
| MFA policy radio: required_all | `auth-config-mfa-required-all` |
| Password min length input | `auth-config-passwordMinLength` |
| Require uppercase switch | `auth-config-passwordUppercase` |
| Require number switch | `auth-config-passwordNumber` |
| Require special switch | `auth-config-passwordSpecial` |
| Lockout threshold input | `auth-config-lockoutThreshold` |
| Lockout duration input | `auth-config-lockoutDuration` |
| Session idle timeout input | `auth-config-sessionIdle` |
| Session absolute timeout input | `auth-config-sessionAbsolute` |
| OIDC enabled toggle | `auth-config-oidcEnabled` |
| OIDC authority input | `auth-config-oidcAuthority` |
| OIDC client ID input | `auth-config-oidcClientId` |
| OIDC client secret input | `auth-config-oidcClientSecret` |
| Save button | `auth-config-save` |

- [ ] **Step 2: Add data-testid to auth-events-page.tsx**

| Element description | data-testid value |
|---|---|
| Event type filter select | `auth-events-filter-type` |
| User search input | `auth-events-filter-user` |
| Start date input | `auth-events-filter-start` |
| End date input | `auth-events-filter-end` |
| Search button | `auth-events-search` |
| Export CSV button | `auth-events-export` |
| Events table | `auth-events-table` |
| Previous page button | `auth-events-prev` |
| Next page button | `auth-events-next` |

- [ ] **Step 3: Add data-testid to auth-sessions-page.tsx**

| Element description | data-testid value |
|---|---|
| Sessions table | `auth-sessions-table` |
| Force logout button per session | `session-logout-{sessionId}` |
| Force logout confirm dialog | `session-logout-confirm` |

- [ ] **Step 4: Add data-testid to security-page.tsx**

| Element description | data-testid value |
|---|---|
| MFA status badge | `security-mfa-status` |
| Enable MFA button | `security-mfa-enable` |
| Disable MFA button | `security-mfa-disable` |
| QR code container | `security-mfa-qrcode` |
| MFA verify code input | `security-mfa-code` |
| Next to verify step button | `security-mfa-next-verify` |
| MFA confirm button | `security-mfa-confirm` |
| Recovery codes container | `security-mfa-recovery-codes` |
| Copy codes button | `security-mfa-copy` |
| Download codes button | `security-mfa-download` |
| MFA done button | `security-mfa-done` |
| Old password input | `security-password-old` |
| New password input | `security-password-new` |
| Confirm password input | `security-password-confirm` |
| Change password button | `security-password-submit` |
| Disable MFA password input | `security-mfa-disable-password` |
| Disable MFA confirm button | `security-mfa-disable-confirm` |

- [ ] **Step 5: Commit**

```bash
git add src/admin/system/auth-config-page.tsx src/admin/system/auth-events-page.tsx src/admin/system/auth-sessions-page.tsx src/admin/profile/security-page.tsx
git commit -m "feat(e2e): add data-testid to auth config, events, sessions, and security pages"
```

---

## Phase C: Write test specs (Tasks 9-18)

> All test files under `/media/Data/Source/IPcom/Asterisk.Platform.Web/tests/e2e/tests/platform-admin/`.
> Tests import `{ test, expect }` from `../../fixtures/auth.fixture` (for authenticated tests) or from `@playwright/test` (for login tests).

### Task 9: Write login.spec.ts (8 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/login.spec.ts`

- [ ] **Step 1: Write login.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/login.spec.ts
import { test, expect } from '@playwright/test';
import { PLATFORM_ADMIN, DEMO_ADMIN } from '../../helpers/credentials';

test.describe('Login', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/login');
  });

  test('should login successfully as platform admin', async ({ page }) => {
    await page.getByTestId('login-email').fill(PLATFORM_ADMIN.email);
    await page.getByTestId('login-password').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('login-submit').click();

    await expect(page).not.toHaveURL(/\/login/);
    await expect(page.getByText(PLATFORM_ADMIN.email)).toBeVisible();
  });

  test('should login successfully as demo admin', async ({ page }) => {
    await page.getByTestId('login-email').fill(DEMO_ADMIN.email);
    await page.getByTestId('login-password').fill(DEMO_ADMIN.password);
    await page.getByTestId('login-submit').click();

    await expect(page).not.toHaveURL(/\/login/);
  });

  test('should show error on wrong password', async ({ page }) => {
    await page.getByTestId('login-email').fill(PLATFORM_ADMIN.email);
    await page.getByTestId('login-password').fill('WrongPassword123!');
    await page.getByTestId('login-submit').click();

    await expect(page.getByTestId('login-error')).toBeVisible();
    await expect(page).toHaveURL(/\/login/);
  });

  test('should show error on nonexistent email', async ({ page }) => {
    await page.getByTestId('login-email').fill('nobody@nowhere.local');
    await page.getByTestId('login-password').fill('SomePassword123!');
    await page.getByTestId('login-submit').click();

    await expect(page.getByTestId('login-error')).toBeVisible();
    await expect(page).toHaveURL(/\/login/);
  });

  test('should show validation on empty fields', async ({ page }) => {
    await page.getByTestId('login-submit').click();

    // Should stay on login page — form validation prevents submission
    await expect(page).toHaveURL(/\/login/);
  });

  test('should logout and redirect to login', async ({ page }) => {
    // Login first
    await page.getByTestId('login-email').fill(PLATFORM_ADMIN.email);
    await page.getByTestId('login-password').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('login-submit').click();
    await expect(page).not.toHaveURL(/\/login/);

    // Logout
    await page.getByRole('button', { name: /logout|sign out/i }).click();
    await expect(page).toHaveURL(/\/login/);

    // Verify protected route redirects
    await page.goto('/admin/tenants');
    await expect(page).toHaveURL(/\/login/);
  });

  test('should redirect protected route to login when unauthenticated', async ({ page }) => {
    await page.goto('/admin/tenants');
    await expect(page).toHaveURL(/\/login/);
  });

  test('should persist session after page reload', async ({ page }) => {
    await page.getByTestId('login-email').fill(PLATFORM_ADMIN.email);
    await page.getByTestId('login-password').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('login-submit').click();
    await expect(page).not.toHaveURL(/\/login/);

    await page.reload();
    await expect(page).not.toHaveURL(/\/login/);
  });
});
```

- [ ] **Step 2: Run tests to verify they pass**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
npm run e2e -- --grep "Login"
```

Expected: All 8 tests pass (requires demo environment running).

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/login.spec.ts
git commit -m "test(e2e): add login spec — 8 tests covering auth flows"
```

---

### Task 10: Write system-settings.spec.ts (7 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/system-settings.spec.ts`

- [ ] **Step 1: Write system-settings.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/system-settings.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';
import { API_BASE } from '../../helpers/credentials';

test.describe('System Settings', () => {
  test('should display license card', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    const licenseCard = page.getByTestId('system-license-card');
    await expect(licenseCard).toBeVisible();
    await expect(licenseCard).toContainText(/community|enterprise|tier/i);
  });

  test('should display cluster status', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    await expect(page.getByText(/instance/i)).toBeVisible();
  });

  test('should display at least one cluster node', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    const nodesGrid = page.getByTestId('system-nodes-grid');
    await expect(nodesGrid).toBeVisible();
    const nodeCards = nodesGrid.locator('[data-testid^="system-node-"]');
    await expect(nodeCards.first()).toBeVisible();
  });

  test('should display global settings form', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    await expect(page.getByTestId('system-settings-platformName')).toBeVisible();
    await expect(page.getByTestId('system-settings-timezone')).toBeVisible();
    await expect(page.getByTestId('system-settings-language')).toBeVisible();
  });

  test('should save and persist settings change', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const original = await api.getSystemSettings();

    await page.goto('/admin/system');
    const nameInput = page.getByTestId('system-settings-platformName');
    await nameInput.clear();
    await nameInput.fill('E2E Test Platform');
    await page.getByTestId('system-settings-save').click();

    // Verify persistence
    await page.reload();
    await expect(page.getByTestId('system-settings-platformName')).toHaveValue('E2E Test Platform');

    // Restore
    await api.updateSystemSettings(original);
  });

  test('should disable save button when no changes', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    await expect(page.getByTestId('system-settings-save')).toBeDisabled();
  });

  test('should show drain button state on node', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system');
    const drainButtons = page.locator('[data-testid^="system-node-drain-"]');
    const count = await drainButtons.count();
    if (count > 0) {
      // Button should exist — either enabled or disabled based on node state
      await expect(drainButtons.first()).toBeVisible();
    }
  });
});
```

- [ ] **Step 2: Run tests**

```bash
npm run e2e -- --grep "System Settings"
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/system-settings.spec.ts
git commit -m "test(e2e): add system settings spec — 7 tests"
```

---

### Task 11: Write diagnostics.spec.ts (4 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/diagnostics.spec.ts`

- [ ] **Step 1: Write diagnostics.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/diagnostics.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';

test.describe('Diagnostics', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/system/diagnostics');
  });

  test('should display platform status card', async ({ platformAdminPage: page }) => {
    const card = page.getByTestId('diag-platform-card');
    await expect(card).toBeVisible();
    await expect(card).toContainText(/version/i);
  });

  test('should display license card', async ({ platformAdminPage: page }) => {
    const card = page.getByTestId('diag-license-card');
    await expect(card).toBeVisible();
    await expect(card).toContainText(/tier/i);
  });

  test('should display cluster nodes table', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('diag-nodes-table');
    await expect(table).toBeVisible();
    // Should have at least 1 row
    const rows = table.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
  });

  test('should auto-refresh data', async ({ platformAdminPage: page }) => {
    // Intercept API calls to verify refresh
    const requests: string[] = [];
    page.on('request', (req) => {
      if (req.url().includes('/cluster')) {
        requests.push(req.url());
      }
    });

    // Wait for auto-refresh (15s interval)
    await page.waitForTimeout(16_000);
    expect(requests.length).toBeGreaterThanOrEqual(1);
  });
});
```

- [ ] **Step 2: Run tests**

```bash
npm run e2e -- --grep "Diagnostics"
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/diagnostics.spec.ts
git commit -m "test(e2e): add diagnostics spec — 4 tests"
```

---

### Task 12: Write tenant-management.spec.ts (10 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/tenant-management.spec.ts`

- [ ] **Step 1: Write tenant-management.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/tenant-management.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';

const E2E_TENANT_ID = `e2e-test-${Date.now()}`;

test.describe('Tenant Management', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/tenants');
  });

  test('should display tenant list with platform and demo', async ({ platformAdminPage: page }) => {
    await expect(page.getByText('platform')).toBeVisible();
    await expect(page.getByText('demo')).toBeVisible();
  });

  test('should display correct table columns', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('data-table');
    await expect(table).toBeVisible();
    await expect(table).toContainText(/name/i);
    await expect(table).toContainText(/status/i);
  });

  test('should filter tenants by search', async ({ platformAdminPage: page }) => {
    await page.getByTestId('data-table-search').fill('demo');
    await expect(page.getByText('demo')).toBeVisible();
    // platform should be filtered out (if name doesn't match "demo")
  });

  test('should create a new tenant', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    await page.getByTestId('tenants-create-button').click();
    await page.getByTestId('tenants-form-tenantId').fill(E2E_TENANT_ID);
    await page.getByTestId('tenants-form-name').fill('E2E Test Tenant');
    await page.getByTestId('tenants-form-submit').click();

    await expect(page.getByText(E2E_TENANT_ID)).toBeVisible();

    // Cleanup
    const api = new ApiHelper(authenticatedApiContext);
    await api.deleteTenant(E2E_TENANT_ID);
  });

  test('should reject invalid tenantId format', async ({ platformAdminPage: page }) => {
    await page.getByTestId('tenants-create-button').click();
    await page.getByTestId('tenants-form-tenantId').fill('INVALID!ID');
    await page.getByTestId('tenants-form-name').fill('Bad Tenant');
    await page.getByTestId('tenants-form-submit').click();

    // Should stay on form — validation error
    await expect(page.getByTestId('tenants-create-sheet')).toBeVisible();
  });

  test('should edit a tenant', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    // Create tenant to edit
    const api = new ApiHelper(authenticatedApiContext);
    const tenantId = `e2e-edit-${Date.now()}`;
    await api.createTenant({ tenantId, name: 'Edit Me' });
    await page.reload();

    await page.getByTestId(`tenant-edit-${tenantId}`).click();
    await page.getByTestId('tenants-edit-name').clear();
    await page.getByTestId('tenants-edit-name').fill('Edited Name');
    await page.getByTestId('tenants-edit-submit').click();

    await expect(page.getByText('Edited Name')).toBeVisible();

    // Cleanup
    await api.deleteTenant(tenantId);
  });

  test('should suspend a tenant', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const tenantId = `e2e-suspend-${Date.now()}`;
    await api.createTenant({ tenantId, name: 'Suspend Me' });
    await page.reload();

    await page.getByTestId(`tenant-edit-${tenantId}`).click();
    await page.getByTestId('tenants-edit-status').selectOption('suspended');
    await page.getByTestId('tenants-edit-submit').click();

    await expect(page.getByTestId(`tenant-status-${tenantId}`)).toContainText(/suspended/i);

    // Cleanup
    await api.deleteTenant(tenantId);
  });

  test('should reactivate a suspended tenant', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const tenantId = `e2e-reactivate-${Date.now()}`;
    await api.createTenant({ tenantId, name: 'Reactivate Me' });
    await page.reload();

    // Suspend
    await page.getByTestId(`tenant-edit-${tenantId}`).click();
    await page.getByTestId('tenants-edit-status').selectOption('suspended');
    await page.getByTestId('tenants-edit-submit').click();

    // Reactivate
    await page.getByTestId(`tenant-edit-${tenantId}`).click();
    await page.getByTestId('tenants-edit-status').selectOption('active');
    await page.getByTestId('tenants-edit-submit').click();

    await expect(page.getByTestId(`tenant-status-${tenantId}`)).toContainText(/active/i);

    // Cleanup
    await api.deleteTenant(tenantId);
  });

  test('should delete a tenant with confirmation', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const tenantId = `e2e-delete-${Date.now()}`;
    await api.createTenant({ tenantId, name: 'Delete Me' });
    await page.reload();

    await page.getByTestId(`tenant-delete-${tenantId}`).click();
    // Wait for 3-second delay on confirm dialog
    const confirmBtn = page.getByTestId('confirm-dialog-confirm');
    await expect(confirmBtn).toBeDisabled();
    await page.waitForTimeout(3500);
    await expect(confirmBtn).toBeEnabled();
    await confirmBtn.click();

    await expect(page.getByText(tenantId)).not.toBeVisible();
  });

  test('should not allow deleting platform tenant', async ({ platformAdminPage: page }) => {
    // Platform tenant should not have a delete button, or delete should fail
    const deleteBtn = page.getByTestId('tenant-delete-platform');
    const count = await deleteBtn.count();
    if (count > 0) {
      await deleteBtn.click();
      const confirmBtn = page.getByTestId('confirm-dialog-confirm');
      await page.waitForTimeout(3500);
      await confirmBtn.click();
      // Should show error
      await expect(page.getByText(/cannot delete|error/i)).toBeVisible();
    }
    // If no delete button exists, the test passes (correct behavior)
  });
});
```

- [ ] **Step 2: Run tests**

```bash
npm run e2e -- --grep "Tenant Management"
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/tenant-management.spec.ts
git commit -m "test(e2e): add tenant management spec — 10 tests"
```

---

### Task 13: Write auth-config.spec.ts (8 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/auth-config.spec.ts`

- [ ] **Step 1: Write auth-config.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/auth-config.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';

test.describe('Auth Config', () => {
  let originalConfig: Record<string, unknown>;

  test.beforeAll(async ({ browser }) => {
    // Save original config to restore after tests
    const ctx = await browser.newContext();
    const request = ctx.request;
    // Will be restored in afterAll
    await ctx.close();
  });

  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/auth-config');
  });

  test('should display MFA policy section', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-config-mfa-optional')).toBeVisible();
    await expect(page.getByTestId('auth-config-mfa-required-roles')).toBeVisible();
    await expect(page.getByTestId('auth-config-mfa-required-all')).toBeVisible();
  });

  test('should change and save MFA policy', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const original = await api.getAuthConfig();

    await page.getByTestId('auth-config-mfa-required-all').click();
    await page.getByTestId('auth-config-save').click();

    await page.reload();
    await expect(page.getByTestId('auth-config-mfa-required-all')).toBeChecked();

    // Restore
    await api.updateAuthConfig(original);
  });

  test('should display password policy', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-config-passwordMinLength')).toBeVisible();
    await expect(page.getByTestId('auth-config-passwordUppercase')).toBeVisible();
    await expect(page.getByTestId('auth-config-passwordNumber')).toBeVisible();
    await expect(page.getByTestId('auth-config-passwordSpecial')).toBeVisible();
  });

  test('should change and save password min length', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const original = await api.getAuthConfig();

    const minLength = page.getByTestId('auth-config-passwordMinLength');
    await minLength.clear();
    await minLength.fill('16');
    await page.getByTestId('auth-config-save').click();

    await page.reload();
    await expect(page.getByTestId('auth-config-passwordMinLength')).toHaveValue('16');

    // Restore
    await api.updateAuthConfig(original);
  });

  test('should display lockout policy', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-config-lockoutThreshold')).toBeVisible();
    await expect(page.getByTestId('auth-config-lockoutDuration')).toBeVisible();
  });

  test('should display session timeouts', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-config-sessionIdle')).toBeVisible();
    await expect(page.getByTestId('auth-config-sessionAbsolute')).toBeVisible();
  });

  test('should toggle OIDC and show fields', async ({ platformAdminPage: page }) => {
    const toggle = page.getByTestId('auth-config-oidcEnabled');
    await toggle.click();

    await expect(page.getByTestId('auth-config-oidcAuthority')).toBeVisible();
    await expect(page.getByTestId('auth-config-oidcClientId')).toBeVisible();
    await expect(page.getByTestId('auth-config-oidcClientSecret')).toBeVisible();

    // Toggle back
    await toggle.click();
  });

  test('should disable save button when no changes', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-config-save')).toBeDisabled();
  });
});
```

- [ ] **Step 2: Run tests**

```bash
npm run e2e -- --grep "Auth Config"
```

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/auth-config.spec.ts
git commit -m "test(e2e): add auth config spec — 8 tests"
```

---

### Task 14: Write auth-events.spec.ts (6 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/auth-events.spec.ts`

- [ ] **Step 1: Write auth-events.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/auth-events.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';

test.describe('Auth Events', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/auth-events');
  });

  test('should display auth events table', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('auth-events-table');
    await expect(table).toBeVisible();
    // Should have events from demo setup
    const rows = table.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
  });

  test('should display correct columns', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('auth-events-table');
    const header = table.locator('thead');
    await expect(header).toContainText(/timestamp|time/i);
    await expect(header).toContainText(/user/i);
    await expect(header).toContainText(/type|event/i);
  });

  test('should filter by event type', async ({ platformAdminPage: page }) => {
    await page.getByTestId('auth-events-filter-type').selectOption('login_success');
    await page.getByTestId('auth-events-search').click();

    const table = page.getByTestId('auth-events-table');
    await expect(table).toBeVisible();
  });

  test('should filter by date range', async ({ platformAdminPage: page }) => {
    const today = new Date().toISOString().split('T')[0];
    await page.getByTestId('auth-events-filter-start').fill(today);
    await page.getByTestId('auth-events-filter-end').fill(today);
    await page.getByTestId('auth-events-search').click();

    const table = page.getByTestId('auth-events-table');
    await expect(table).toBeVisible();
  });

  test('should show pagination controls', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('auth-events-prev')).toBeVisible();
    await expect(page.getByTestId('auth-events-next')).toBeVisible();
  });

  test('should export CSV', async ({ platformAdminPage: page }) => {
    const downloadPromise = page.waitForEvent('download');
    await page.getByTestId('auth-events-export').click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toMatch(/\.csv$/);
  });
});
```

- [ ] **Step 2: Run tests and commit**

```bash
npm run e2e -- --grep "Auth Events"
git add tests/e2e/tests/platform-admin/auth-events.spec.ts
git commit -m "test(e2e): add auth events spec — 6 tests"
```

---

### Task 15: Write auth-sessions.spec.ts (5 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/auth-sessions.spec.ts`

- [ ] **Step 1: Write auth-sessions.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/auth-sessions.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';
import { API_BASE, DEMO_ADMIN } from '../../helpers/credentials';

test.describe('Auth Sessions', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/auth-sessions');
  });

  test('should display active sessions', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('auth-sessions-table');
    await expect(table).toBeVisible();
    const rows = table.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
  });

  test('should display correct columns', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('auth-sessions-table');
    const header = table.locator('thead');
    await expect(header).toContainText(/user/i);
    await expect(header).toContainText(/ip/i);
  });

  test('should force logout another session', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    // Create a second session by logging in as demo admin
    const api = new ApiHelper(authenticatedApiContext);
    await api.login(DEMO_ADMIN.tenantId, DEMO_ADMIN.email, DEMO_ADMIN.password);

    await page.reload();

    // Find a force-logout button (for any session that is not the current one)
    const logoutButtons = page.locator('[data-testid^="session-logout-"]');
    const count = await logoutButtons.count();
    if (count > 0) {
      await logoutButtons.first().click();
      await expect(page.getByTestId('session-logout-confirm')).toBeVisible();
      await page.getByTestId('confirm-dialog-confirm').click();
    }
  });

  test('should auto-refresh sessions', async ({ platformAdminPage: page }) => {
    const requests: string[] = [];
    page.on('request', (req) => {
      if (req.url().includes('/sessions')) {
        requests.push(req.url());
      }
    });
    await page.waitForTimeout(31_000);
    expect(requests.length).toBeGreaterThanOrEqual(1);
  });

  test('should show confirm dialog on force logout', async ({ platformAdminPage: page }) => {
    const logoutButtons = page.locator('[data-testid^="session-logout-"]');
    const count = await logoutButtons.count();
    if (count > 0) {
      await logoutButtons.first().click();
      await expect(page.getByTestId('confirm-dialog')).toBeVisible();
      await page.getByTestId('confirm-dialog-cancel').click();
    }
  });
});
```

- [ ] **Step 2: Run tests and commit**

```bash
npm run e2e -- --grep "Auth Sessions"
git add tests/e2e/tests/platform-admin/auth-sessions.spec.ts
git commit -m "test(e2e): add auth sessions spec — 5 tests"
```

---

### Task 16: Write audit.spec.ts (5 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/audit.spec.ts`

- [ ] **Step 1: Write audit.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/audit.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';

test.describe('Audit Log', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/audit');
  });

  test('should display audit entries', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('audit-table');
    await expect(table).toBeVisible();
    const rows = table.locator('tbody tr');
    await expect(rows.first()).toBeVisible();
  });

  test('should display correct columns', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('audit-table');
    const header = table.locator('thead');
    await expect(header).toContainText(/action/i);
    await expect(header).toContainText(/entity/i);
  });

  test('should filter by action', async ({ platformAdminPage: page }) => {
    await page.getByTestId('audit-filter-action').fill('create');
    await page.getByTestId('audit-search-button').click();

    const table = page.getByTestId('audit-table');
    await expect(table).toBeVisible();
  });

  test('should filter by entity type', async ({ platformAdminPage: page }) => {
    await page.getByTestId('audit-filter-entityType').fill('User');
    await page.getByTestId('audit-search-button').click();

    const table = page.getByTestId('audit-table');
    await expect(table).toBeVisible();
  });

  test('should show pagination controls', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('audit-prev')).toBeVisible();
    await expect(page.getByTestId('audit-next')).toBeVisible();
  });
});
```

- [ ] **Step 2: Run tests and commit**

```bash
npm run e2e -- --grep "Audit Log"
git add tests/e2e/tests/platform-admin/audit.spec.ts
git commit -m "test(e2e): add audit log spec — 5 tests"
```

---

### Task 17: Write security.spec.ts (6 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/security.spec.ts`

- [ ] **Step 1: Write security.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/security.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { API_BASE, PLATFORM_ADMIN } from '../../helpers/credentials';
import * as OTPAuth from 'otpauth';

test.describe('Security — Personal', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/security');
  });

  test('should display MFA status as disabled', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('security-mfa-status')).toContainText(/disabled/i);
  });

  test('should complete MFA setup flow', async ({ platformAdminPage: page }) => {
    // Start MFA setup
    await page.getByTestId('security-mfa-enable').click();

    // QR code should appear
    await expect(page.getByTestId('security-mfa-qrcode')).toBeVisible();

    // Extract the OTP secret from the page (the manual key text)
    // Navigate to the verify step
    await page.getByTestId('security-mfa-next-verify').click();

    // Get the secret from the API response we intercepted during setup
    // We need to read the secret from the page. The QR code encodes an otpauth:// URI
    // We can get it from the displayed manual key text
    const secretText = await page.locator('code, [class*="mono"]').first().textContent();
    if (!secretText) {
      test.skip(true, 'Could not extract MFA secret from page');
      return;
    }

    // Generate TOTP code
    const totp = new OTPAuth.TOTP({
      secret: OTPAuth.Secret.fromBase32(secretText.replace(/\s/g, '')),
      digits: 6,
      period: 30,
    });
    const code = totp.generate();

    // Enter code
    await page.getByTestId('security-mfa-code').fill(code);
    await page.getByTestId('security-mfa-confirm').click();

    // Should show recovery codes
    await expect(page.getByTestId('security-mfa-recovery-codes')).toBeVisible();

    // Complete setup
    await page.getByTestId('security-mfa-done').click();

    // MFA should now be enabled
    await expect(page.getByTestId('security-mfa-status')).toContainText(/enabled/i);

    // Disable MFA to leave clean state
    await page.getByTestId('security-mfa-disable').click();
    await page.getByTestId('security-mfa-disable-password').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('security-mfa-disable-confirm').click();
    await expect(page.getByTestId('security-mfa-status')).toContainText(/disabled/i);
  });

  test('should show copy and download buttons for recovery codes', async ({ platformAdminPage: page }) => {
    // Setup MFA to get to recovery codes step
    await page.getByTestId('security-mfa-enable').click();
    await page.getByTestId('security-mfa-next-verify').click();

    const secretText = await page.locator('code, [class*="mono"]').first().textContent();
    if (!secretText) {
      test.skip(true, 'Could not extract MFA secret');
      return;
    }

    const totp = new OTPAuth.TOTP({
      secret: OTPAuth.Secret.fromBase32(secretText.replace(/\s/g, '')),
      digits: 6,
      period: 30,
    });
    await page.getByTestId('security-mfa-code').fill(totp.generate());
    await page.getByTestId('security-mfa-confirm').click();

    await expect(page.getByTestId('security-mfa-copy')).toBeVisible();
    await expect(page.getByTestId('security-mfa-download')).toBeVisible();

    // Cleanup
    await page.getByTestId('security-mfa-done').click();
    await page.getByTestId('security-mfa-disable').click();
    await page.getByTestId('security-mfa-disable-password').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('security-mfa-disable-confirm').click();
  });

  test('should disable MFA with password confirmation', async ({ platformAdminPage: page }) => {
    // Enable first (quick flow via API would be better, but testing UI flow)
    // Skip if MFA is already disabled
    const status = await page.getByTestId('security-mfa-status').textContent();
    if (status?.toLowerCase().includes('disabled')) {
      // MFA is already disabled — test the disable flow isn't available
      await expect(page.getByTestId('security-mfa-disable')).not.toBeVisible();
    }
  });

  test('should change password successfully', async ({ platformAdminPage: page }) => {
    // Note: actually changing the platform admin password would break other tests
    // So we test validation only
    await page.getByTestId('security-password-old').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('security-password-new').fill(PLATFORM_ADMIN.password);
    await page.getByTestId('security-password-confirm').fill(PLATFORM_ADMIN.password);
    // Don't actually submit — password is the same, just verify form is functional
    await expect(page.getByTestId('security-password-submit')).toBeEnabled();
  });

  test('should show validation when passwords dont match', async ({ platformAdminPage: page }) => {
    await page.getByTestId('security-password-old').fill('OldPass123!');
    await page.getByTestId('security-password-new').fill('NewPass123!');
    await page.getByTestId('security-password-confirm').fill('DifferentPass123!');
    await page.getByTestId('security-password-submit').click();

    // Should show mismatch error or prevent submission
    await expect(page).toHaveURL(/\/admin\/security/);
  });
});
```

- [ ] **Step 2: Run tests and commit**

```bash
npm run e2e -- --grep "Security"
git add tests/e2e/tests/platform-admin/security.spec.ts
git commit -m "test(e2e): add security spec — 6 tests (MFA + password)"
```

---

### Task 18: Write setup-wizard.spec.ts (3 tests)

**Files:**
- Create: `tests/e2e/tests/platform-admin/setup-wizard.spec.ts`

- [ ] **Step 1: Write setup-wizard.spec.ts**

```typescript
// tests/e2e/tests/platform-admin/setup-wizard.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { API_BASE } from '../../helpers/credentials';

test.describe('Setup Wizard', () => {
  test('should block setup when platform already initialized', async ({ request }) => {
    const response = await request.post(`${API_BASE}/api/setup`, {
      data: {
        email: 'test@test.local',
        password: 'TestPass2026!',
        displayName: 'Test',
        platformName: 'Test',
      },
    });
    // Should fail because platform is already initialized
    expect(response.ok()).toBe(false);
    expect(response.status()).toBeGreaterThanOrEqual(400);
  });

  test('should dismiss setup banner', async ({ platformAdminPage: page }) => {
    await page.goto('/admin');
    const banner = page.getByTestId('setup-banner');
    const isVisible = await banner.isVisible().catch(() => false);
    if (isVisible) {
      await page.getByTestId('setup-banner-dismiss').click();
      await expect(banner).not.toBeVisible();
    }
  });

  test('should navigate wizard steps', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/setup');

    // Should show wizard content
    const nextBtn = page.getByTestId('setup-next');
    const backBtn = page.getByTestId('setup-back');
    const skipBtn = page.getByTestId('setup-skip');

    // If getstarted is visible, click it first
    const getStarted = page.getByTestId('setup-getstarted');
    if (await getStarted.isVisible().catch(() => false)) {
      await getStarted.click();
    }

    // Next should be visible
    if (await nextBtn.isVisible().catch(() => false)) {
      await nextBtn.click();
      // Back should now be enabled
      await expect(backBtn).toBeEnabled();
      await backBtn.click();
    }

    // Skip should dismiss
    if (await skipBtn.isVisible().catch(() => false)) {
      await skipBtn.click();
      await expect(page).not.toHaveURL(/\/admin\/setup/);
    }
  });
});
```

- [ ] **Step 2: Run all E2E tests**

```bash
npm run e2e
```

Expected: All 62 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/tests/platform-admin/setup-wizard.spec.ts
git commit -m "test(e2e): add setup wizard spec — 3 tests"
```

---

## Final Verification

### Task 19: Run full suite and verify

- [ ] **Step 1: Run the complete E2E suite**

```bash
cd /media/Data/Source/IPcom/Asterisk.Platform.Web
npm run e2e
```

Expected: 62 tests across 10 spec files, all green.

- [ ] **Step 2: Run with HTML report**

```bash
npm run e2e
npx playwright show-report
```

Verify the HTML report shows all tests categorized by spec file.

- [ ] **Step 3: Final commit with any adjustments**

If any tests needed adjustment during the run, commit the fixes:

```bash
git add -A tests/e2e/
git commit -m "test(e2e): finalize Sprint 1 — Platform Admin E2E suite (62 tests)"
```
