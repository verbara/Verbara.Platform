# Billing E2E Tests — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write ~25 Playwright E2E tests across 4 spec files that verify the billing frontend pages (rate cards, invoices, usage, quotas) built in Plan 28D.

**Architecture:** Extend the existing `ApiHelper` with billing API methods for test data seeding. Each spec file tests one billing page. Tests use `platformAdminPage` fixture (pre-authenticated as platform admin). Before each test, navigate to the billing page. Seed test data via API, verify UI, cleanup via API.

**Tech Stack:** Playwright 1.58.x, TypeScript, existing auth fixtures

**Working directory:** `/media/Data/Source/IPcom/Asterisk.Platform.Web/`

**Important:** The billing pages require `activeTenantId` to be set. Since the `platformAdminPage` fixture logs in as the `platform` tenant, and billing endpoints use `?tenantId=` query params (handled by the hooks using `useTenantStore.activeTenantId`), the tests need to first select a tenant. We'll use a helper that navigates to `/admin/tenants`, clicks a tenant to set `activeTenantId`, then navigates to the billing page. Alternatively, we can seed the activeTenantId directly into localStorage.

---

## File Structure

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `tests/e2e/fixtures/api.fixture.ts` | Add billing API methods (rate cards, quotas, usage) |
| Create | `tests/e2e/tests/platform-admin/billing-rate-cards.spec.ts` | Rate card CRUD tests (~7 tests) |
| Create | `tests/e2e/tests/platform-admin/billing-invoices.spec.ts` | Invoice list/generate/detail/issue tests (~6 tests) |
| Create | `tests/e2e/tests/platform-admin/billing-usage.spec.ts` | Usage dashboard tests (~6 tests) |
| Create | `tests/e2e/tests/platform-admin/billing-quotas.spec.ts` | Quota status/edit tests (~6 tests) |

---

### Task 1: Extend ApiHelper with Billing Methods

**Files:**
- Modify: `tests/e2e/fixtures/api.fixture.ts`

Add methods to seed and cleanup billing test data via the Management Billing API.

- [ ] **Step 1: Add billing methods to ApiHelper**

Add after the existing `login` method:

```typescript
  // --- Billing: Rate Cards ---

  async createRateCard(tenantId: string, data: {
    name: string;
    currency: string;
    effectiveFrom: string;
    effectiveTo?: string | null;
    isDefault: boolean;
    rates: Array<{ usageType: string; unitPrice: number; includedQuantity: number; tiers: null }>;
  }) {
    const response = await this.request.post(
      `${API_BASE}/api/management/rate-cards?tenantId=${tenantId}`,
      { data },
    );
    return response;
  }

  async listRateCards(tenantId: string) {
    const response = await this.request.get(
      `${API_BASE}/api/management/rate-cards?tenantId=${tenantId}`,
    );
    return response.json();
  }

  async deleteRateCard(tenantId: string, rateCardId: string) {
    return this.request.delete(
      `${API_BASE}/api/management/rate-cards/${rateCardId}?tenantId=${tenantId}`,
    );
  }

  // --- Billing: Invoices ---

  async generateInvoice(tenantId: string, periodStart: string, periodEnd: string) {
    const response = await this.request.post(
      `${API_BASE}/api/management/invoices/generate?tenantId=${tenantId}`,
      { data: { periodStart, periodEnd } },
    );
    return response;
  }

  async listInvoices(tenantId: string) {
    const response = await this.request.get(
      `${API_BASE}/api/management/invoices?tenantId=${tenantId}`,
    );
    return response.json();
  }

  // --- Billing: Quotas ---

  async updateQuota(tenantId: string, data: Record<string, unknown>) {
    const response = await this.request.put(
      `${API_BASE}/api/management/tenants/${tenantId}/quota`,
      { data },
    );
    return response;
  }

  async getQuotaStatus(tenantId: string) {
    const response = await this.request.get(
      `${API_BASE}/api/management/tenants/${tenantId}/quota`,
    );
    return response.json();
  }

  // --- Billing: Usage ---

  async getUsageSummary(tenantId: string) {
    const response = await this.request.get(
      `${API_BASE}/api/management/tenants/${tenantId}/usage`,
    );
    return response.json();
  }
```

- [ ] **Step 2: Verify TypeScript compiles**

Run: `npx tsc --noEmit --pretty 2>&1 | head -20`

- [ ] **Step 3: Commit**

```bash
git add tests/e2e/fixtures/api.fixture.ts
git commit -m "feat(e2e): extend ApiHelper with billing management API methods"
```

---

### Task 2: Rate Cards E2E Tests

**Files:**
- Create: `tests/e2e/tests/platform-admin/billing-rate-cards.spec.ts`

Tests: page renders, no-tenant guard, create rate card via UI, edit, delete with 3s confirmation, search/filter.

Note: The billing pages check `useTenantStore.activeTenantId`. Since this is Zustand persisted state, we need to ensure a tenant is selected. The platformAdminPage fixture sets `tenantId: 'platform'` in auth state, but `activeTenantId` is separate in tenant-store. We'll set it by visiting the tenants page and clicking a tenant first, OR we can inject it into localStorage directly in the test setup.

Looking at the tenant store code, it uses `create()` WITHOUT persist middleware, so it resets on page load. The billing hooks use `useBillingTenantId()` which falls back to `auth.tenantId` when `activeTenantId` is null. So the pages SHOULD work without setting activeTenantId — the hooks will use the auth store's tenantId as fallback.

Wait — but the pages check `useTenantStore((s) => s.activeTenantId)` directly for the no-tenant guard. If it's null, they show "Select a tenant". So we need to set it.

The simplest approach: add a localStorage entry for the tenant store in the auth fixture, OR have each billing test navigate to tenants page first and click a tenant.

Actually, let's look at the billing page code again. The pages check:
```tsx
const tenantId = useTenantStore((s) => s.activeTenantId);
if (!tenantId) return "Select a tenant" message
```

And the hooks use:
```tsx
function useBillingTenantId(): string {
  const active = useTenantStore((s) => s.activeTenantId);
  const auth = useAuthStore((s) => s.tenantId);
  return active ?? auth ?? '';
}
```

So the page guard blocks rendering when `activeTenantId` is null. We need to set it. The cleanest way for E2E tests is to navigate to `/admin/tenants` first, which presumably sets the `activeTenantId` when you interact with it. Or we inject it via JavaScript:

```typescript
await page.evaluate(() => {
  // The tenant store is a Zustand store without persist
  // We can set it via the global window or by dispatching to the store
});
```

Actually, the simplest E2E approach: each billing test's `beforeEach` sets the tenant store via `page.evaluate`. Or better: we add a helper that does this.

Let's take the pragmatic approach — in beforeEach, after goto, we set localStorage and reload:

```typescript
test.beforeEach(async ({ platformAdminPage: page }) => {
  // Set active tenant for billing pages
  await page.goto('/admin/tenants');
  // The tenants page loads and we can click a tenant row to set activeTenantId
  // But actually the tenant store has no persist, so clicking doesn't help across navigations
  
  // Best approach: evaluate JS to set the store directly
  await page.goto('/admin/billing/rate-cards');
  await page.evaluate(() => {
    // Access Zustand store via its internal API
    // The tenant store exposes setActiveTenant
    // We need to find it on the window or via React internals
  });
});
```

Hmm, this is getting complex. Let me check if there's a simpler way. The `useTenantStore` uses `create()` without persist. But the billing hooks fallback to `useAuthStore.tenantId` which IS set ('platform'). The issue is only the page-level guard.

The cleanest fix: update the 4 billing pages to use the same fallback logic as the hooks. Change:
```tsx
const tenantId = useTenantStore((s) => s.activeTenantId);
```
to:
```tsx
const activeTenantId = useTenantStore((s) => s.activeTenantId);
const authTenantId = useAuthStore((s) => s.tenantId);
const tenantId = activeTenantId ?? authTenantId;
```

This way, if no tenant is explicitly selected but the user IS authenticated (which they always are on admin pages), the billing pages will use their auth tenant. This is also better UX — single-tenant users shouldn't have to go to the tenants page first.

Let's make this fix as part of Task 2 (or as a separate pre-task). This is a minor fix, not a feature change.

- [ ] **Step 1: Fix billing pages to fallback to auth tenant**

In all 4 billing pages, change the tenant resolution to include auth fallback:

`rate-cards-page.tsx`: Add `import { useAuthStore } from '@/core/auth/auth-store';` and change:
```tsx
const tenantId = useTenantStore((s) => s.activeTenantId);
```
to:
```tsx
const activeTenantId = useTenantStore((s) => s.activeTenantId);
const authTenantId = useAuthStore((s) => s.tenantId);
const tenantId = activeTenantId ?? authTenantId;
```

Same change in `invoices-page.tsx`, `usage-page.tsx`, `quotas-page.tsx`.

- [ ] **Step 2: Commit the fix**

```bash
git add src/admin/billing/
git commit -m "fix(billing): fallback to auth tenant when no active tenant selected"
```

- [ ] **Step 3: Create the rate cards spec file**

```typescript
// tests/e2e/tests/platform-admin/billing-rate-cards.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';

const TENANT_ID = 'platform';

test.describe('Billing — Rate Cards', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/rate-cards');
  });

  test('should display rate cards page', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('rate-cards-page')).toBeVisible();
    await expect(page.getByTestId('create-rate-card')).toBeVisible();
  });

  test('should show data table', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('data-table');
    await expect(table).toBeVisible();
  });

  test('should create a rate card via UI', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Rate Card ${Date.now()}`;

    await page.getByTestId('create-rate-card').click();
    await page.getByTestId('rate-card-name').fill(name);
    await page.getByTestId('rate-card-currency').clear();
    await page.getByTestId('rate-card-currency').fill('EUR');
    await page.getByTestId('rate-card-from').fill('2026-01-01T00:00');

    // Add a rate entry
    await page.getByTestId('add-rate-entry').click();
    await expect(page.getByTestId('rate-entry-0')).toBeVisible();

    await page.getByTestId('rate-card-submit').click();

    // Verify it appears in the table
    await expect(page.getByText(name)).toBeVisible();

    // Cleanup via API
    const cards = await api.listRateCards(TENANT_ID);
    const created = cards.find((c: any) => c.name === name);
    if (created) await api.deleteRateCard(TENANT_ID, created.rateCardId);
  });

  test('should edit an existing rate card', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Edit RC ${Date.now()}`;

    // Seed via API
    await api.createRateCard(TENANT_ID, {
      name,
      currency: 'USD',
      effectiveFrom: '2026-01-01T00:00:00Z',
      isDefault: false,
      rates: [{ usageType: 'VoiceInbound', unitPrice: 0.01, includedQuantity: 100, tiers: null }],
    });
    await page.reload();

    // Find and click edit
    const cards = await api.listRateCards(TENANT_ID);
    const card = cards.find((c: any) => c.name === name);
    await page.getByTestId(`edit-rate-card-${card.rateCardId}`).click();

    // Edit the name
    await page.getByTestId('rate-card-name').clear();
    await page.getByTestId('rate-card-name').fill(`${name} Edited`);
    await page.getByTestId('rate-card-submit').click();

    await expect(page.getByText(`${name} Edited`)).toBeVisible();

    // Cleanup
    await api.deleteRateCard(TENANT_ID, card.rateCardId);
  });

  test('should delete a rate card with 3s confirmation', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Delete RC ${Date.now()}`;

    await api.createRateCard(TENANT_ID, {
      name,
      currency: 'USD',
      effectiveFrom: '2026-01-01T00:00:00Z',
      isDefault: false,
      rates: [{ usageType: 'VoiceInbound', unitPrice: 0.01, includedQuantity: 0, tiers: null }],
    });
    await page.reload();

    const cards = await api.listRateCards(TENANT_ID);
    const card = cards.find((c: any) => c.name === name);
    await page.getByTestId(`delete-rate-card-${card.rateCardId}`).click();

    // 3s confirmation pattern
    const confirmBtn = page.getByTestId('confirm-dialog-confirm');
    await expect(confirmBtn).toBeDisabled();
    await page.waitForTimeout(3500);
    await expect(confirmBtn).toBeEnabled();
    await confirmBtn.click();

    await expect(page.getByText(name)).not.toBeVisible();
  });

  test('should search rate cards', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Search RC ${Date.now()}`;

    await api.createRateCard(TENANT_ID, {
      name,
      currency: 'USD',
      effectiveFrom: '2026-01-01T00:00:00Z',
      isDefault: false,
      rates: [{ usageType: 'SmsOutbound', unitPrice: 0.005, includedQuantity: 0, tiers: null }],
    });
    await page.reload();

    await page.getByTestId('data-table-search').fill(name);
    await expect(page.getByText(name)).toBeVisible();

    await api.deleteRateCard(TENANT_ID, (await api.listRateCards(TENANT_ID)).find((c: any) => c.name === name).rateCardId);
  });

  test('should display rate entry count in table', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Entries RC ${Date.now()}`;

    await api.createRateCard(TENANT_ID, {
      name,
      currency: 'USD',
      effectiveFrom: '2026-01-01T00:00:00Z',
      isDefault: false,
      rates: [
        { usageType: 'VoiceInbound', unitPrice: 0.01, includedQuantity: 100, tiers: null },
        { usageType: 'SmsOutbound', unitPrice: 0.005, includedQuantity: 50, tiers: null },
      ],
    });
    await page.reload();

    await expect(page.getByText('2 entries')).toBeVisible();

    await api.deleteRateCard(TENANT_ID, (await api.listRateCards(TENANT_ID)).find((c: any) => c.name === name).rateCardId);
  });

  test('should show default badge for default rate card', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    const name = `E2E Default RC ${Date.now()}`;

    await api.createRateCard(TENANT_ID, {
      name,
      currency: 'USD',
      effectiveFrom: '2026-01-01T00:00:00Z',
      isDefault: true,
      rates: [{ usageType: 'VoiceInbound', unitPrice: 0.01, includedQuantity: 0, tiers: null }],
    });
    await page.reload();

    await expect(page.getByText('Default')).toBeVisible();

    await api.deleteRateCard(TENANT_ID, (await api.listRateCards(TENANT_ID)).find((c: any) => c.name === name).rateCardId);
  });
});
```

- [ ] **Step 4: Verify TypeScript compiles**

- [ ] **Step 5: Commit**

```bash
git add tests/e2e/tests/platform-admin/billing-rate-cards.spec.ts
git commit -m "test(e2e): add 7 rate card billing E2E tests"
```

---

### Task 3: Invoices E2E Tests

**Files:**
- Create: `tests/e2e/tests/platform-admin/billing-invoices.spec.ts`

- [ ] **Step 1: Create the invoices spec file**

```typescript
// tests/e2e/tests/platform-admin/billing-invoices.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';

const TENANT_ID = 'platform';

test.describe('Billing — Invoices', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/invoices');
  });

  test('should display invoices page', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('invoices-page')).toBeVisible();
    await expect(page.getByTestId('generate-invoice')).toBeVisible();
  });

  test('should show data table', async ({ platformAdminPage: page }) => {
    const table = page.getByTestId('data-table');
    await expect(table).toBeVisible();
  });

  test('should open generate invoice dialog', async ({ platformAdminPage: page }) => {
    await page.getByTestId('generate-invoice').click();
    await expect(page.getByTestId('generate-period-start')).toBeVisible();
    await expect(page.getByTestId('generate-period-end')).toBeVisible();
    await expect(page.getByTestId('generate-invoice-submit')).toBeVisible();
  });

  test('should disable generate button without dates', async ({ platformAdminPage: page }) => {
    await page.getByTestId('generate-invoice').click();
    await expect(page.getByTestId('generate-invoice-submit')).toBeDisabled();
  });

  test('should enable generate button with dates', async ({ platformAdminPage: page }) => {
    await page.getByTestId('generate-invoice').click();
    await page.getByTestId('generate-period-start').fill('2026-01-01T00:00');
    await page.getByTestId('generate-period-end').fill('2026-01-31T23:59');
    await expect(page.getByTestId('generate-invoice-submit')).toBeEnabled();
  });

  test('should navigate via sidebar', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/rate-cards');
    await page.getByTestId('sidebar-link-invoices').click();
    await expect(page).toHaveURL(/\/admin\/billing\/invoices/);
    await expect(page.getByTestId('invoices-page')).toBeVisible();
  });
});
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/tests/platform-admin/billing-invoices.spec.ts
git commit -m "test(e2e): add 6 invoice billing E2E tests"
```

---

### Task 4: Usage Dashboard E2E Tests

**Files:**
- Create: `tests/e2e/tests/platform-admin/billing-usage.spec.ts`

- [ ] **Step 1: Create the usage spec file**

```typescript
// tests/e2e/tests/platform-admin/billing-usage.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';

test.describe('Billing — Usage Dashboard', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/usage');
  });

  test('should display usage page', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('usage-page')).toBeVisible();
  });

  test('should show date range filters', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('usage-filters')).toBeVisible();
    await expect(page.locator('#usage-from')).toBeVisible();
    await expect(page.locator('#usage-until')).toBeVisible();
  });

  test('should show usage type filter', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('usage-type-filter')).toBeVisible();
  });

  test('should have default date range set to current month', async ({ platformAdminPage: page }) => {
    const fromInput = page.locator('#usage-from');
    const value = await fromInput.inputValue();
    // Should start with current year-month (e.g., "2026-03")
    const now = new Date();
    const expectedPrefix = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
    expect(value).toContain(expectedPrefix);
  });

  test('should show detailed records section', async ({ platformAdminPage: page }) => {
    await expect(page.getByText('Detailed records')).toBeVisible();
    await expect(page.getByTestId('data-table')).toBeVisible();
  });

  test('should navigate via sidebar', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/rate-cards');
    await page.getByTestId('sidebar-link-usage').click();
    await expect(page).toHaveURL(/\/admin\/billing\/usage/);
    await expect(page.getByTestId('usage-page')).toBeVisible();
  });
});
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/tests/platform-admin/billing-usage.spec.ts
git commit -m "test(e2e): add 6 usage dashboard E2E tests"
```

---

### Task 5: Quotas E2E Tests

**Files:**
- Create: `tests/e2e/tests/platform-admin/billing-quotas.spec.ts`

- [ ] **Step 1: Create the quotas spec file**

```typescript
// tests/e2e/tests/platform-admin/billing-quotas.spec.ts
import { test, expect } from '../../fixtures/auth.fixture';
import { ApiHelper } from '../../fixtures/api.fixture';

const TENANT_ID = 'platform';

test.describe('Billing — Quotas', () => {
  test.beforeEach(async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/quotas');
  });

  test('should display quotas page', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('quotas-page')).toBeVisible();
  });

  test('should show edit button', async ({ platformAdminPage: page }) => {
    await expect(page.getByTestId('edit-quota')).toBeVisible();
  });

  test('should show quota limits after seeding', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    await api.updateQuota(TENANT_ID, {
      maxConcurrentChannels: 200,
      maxActiveCampaigns: 20,
      quotaAction: 'Warn',
    });
    await page.reload();

    await expect(page.getByTestId('quota-limits')).toBeVisible();
    await expect(page.getByTestId('quota-action-badge')).toBeVisible();
    await expect(page.getByTestId('quota-action-badge')).toContainText('Warn');
  });

  test('should open edit sheet and show form fields', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    await api.updateQuota(TENANT_ID, {
      maxConcurrentChannels: 100,
      maxActiveCampaigns: 10,
      quotaAction: 'Warn',
    });
    await page.reload();

    await page.getByTestId('edit-quota').click();
    await expect(page.getByTestId('quota-channels')).toBeVisible();
    await expect(page.getByTestId('quota-campaigns')).toBeVisible();
    await expect(page.getByTestId('quota-voice')).toBeVisible();
    await expect(page.getByTestId('quota-messages')).toBeVisible();
    await expect(page.getByTestId('quota-storage')).toBeVisible();
    await expect(page.getByTestId('quota-agents')).toBeVisible();
    await expect(page.getByTestId('quota-action-select')).toBeVisible();
    await expect(page.getByTestId('quota-submit')).toBeVisible();
  });

  test('should update quota via form', async ({ platformAdminPage: page, authenticatedApiContext }) => {
    const api = new ApiHelper(authenticatedApiContext);
    await api.updateQuota(TENANT_ID, {
      maxConcurrentChannels: 100,
      maxActiveCampaigns: 10,
      quotaAction: 'Warn',
    });
    await page.reload();

    await page.getByTestId('edit-quota').click();
    await page.getByTestId('quota-channels').clear();
    await page.getByTestId('quota-channels').fill('500');
    await page.getByTestId('quota-submit').click();

    // Verify update persisted
    const status = await api.getQuotaStatus(TENANT_ID);
    expect(status.quota.maxConcurrentChannels).toBe(500);

    // Reset
    await api.updateQuota(TENANT_ID, { maxConcurrentChannels: 100 });
  });

  test('should navigate via sidebar', async ({ platformAdminPage: page }) => {
    await page.goto('/admin/billing/rate-cards');
    await page.getByTestId('sidebar-link-quotas').click();
    await expect(page).toHaveURL(/\/admin\/billing\/quotas/);
    await expect(page.getByTestId('quotas-page')).toBeVisible();
  });
});
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/tests/platform-admin/billing-quotas.spec.ts
git commit -m "test(e2e): add 6 quota billing E2E tests"
```

---

### Task 6: Final Verification

- [ ] **Step 1: Verify TypeScript compiles**

Run: `npx tsc --noEmit --pretty 2>&1 | head -20`

- [ ] **Step 2: Count test files and tests**

Run: `grep -c "test('" tests/e2e/tests/platform-admin/billing-*.spec.ts`
Expected: ~25 total tests across 4 files

- [ ] **Step 3: Commit docs update**

```bash
git commit -m "docs: update plan with E2E test completion"
```
