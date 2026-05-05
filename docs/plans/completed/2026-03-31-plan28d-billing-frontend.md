# Billing Frontend Pages — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build 5 billing management pages in Platform.Web (React 19) that consume the 12 Management Billing API endpoints built in Plan 28C.

**Architecture:** New `src/admin/billing/` directory with 5 page files + 1 form component + 1 API hooks file. Pages follow the existing admin pattern: PageHeader + DataTable + Sheet forms. All pages require `system:tenant:configure` permission and use `activeTenantId` from tenant store (same as other management pages). Billing API endpoints use `?tenantId=` query params (rate cards, invoices) or `{tenantId}` path params (usage, quotas).

**Tech Stack:** React 19, TypeScript, TanStack Query 5, TanStack Table 8, React Hook Form 7 + Zod 4, TailwindCSS v4, Lucide React icons, date-fns 4, Recharts 3 (usage charts), shadcn/ui v4 (`@base-ui/react`, NOT Radix)

**Working directory:** `/media/Data/Source/Verbara/Asterisk.Platform.Web/`

---

## File Structure

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/core/api/hooks/use-billing.ts` | 15 TanStack Query hooks + TypeScript types for all billing DTOs |
| Create | `src/admin/billing/rate-cards-page.tsx` | Rate card list + create/edit/delete CRUD |
| Create | `src/admin/billing/rate-card-form.tsx` | Rate card Sheet form with useFieldArray for rate entries |
| Create | `src/admin/billing/invoices-page.tsx` | Invoice list + generate + view detail + issue |
| Create | `src/admin/billing/usage-page.tsx` | Usage summary cards + detailed records table with filters |
| Create | `src/admin/billing/quotas-page.tsx` | Quota status display + update form |
| Modify | `src/router.tsx` | Add 5 billing routes under `/admin/billing/*` |
| Modify | `src/admin/sidebar.tsx` | Add "Billing" group with 4 sidebar items |

---

### Task 1: API Types + TanStack Query Hooks

**Files:**
- Create: `src/core/api/hooks/use-billing.ts`

This task creates all TypeScript interfaces matching the backend DTOs and 15 TanStack Query hooks for the 12 billing API endpoints.

- [ ] **Step 1: Create the billing hooks file with types and all hooks**

```typescript
// src/core/api/hooks/use-billing.ts
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { customFetch } from '@/core/api/client';
import { useTenantStore } from '@/core/tenant/tenant-store';
import { useAuthStore } from '@/core/auth/auth-store';
import { toast } from 'sonner';

// --- Types ---

export interface RateTier {
  fromQuantity: number;
  toQuantity: number | null;
  unitPrice: number;
}

export interface RateEntry {
  usageType: string;
  unitPrice: number;
  includedQuantity: number;
  tiers: RateTier[] | null;
}

export interface RateCard {
  rateCardId: string;
  tenantId: string;
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  isDefault: boolean;
  rates: RateEntry[];
}

export interface CreateRateCardInput {
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  isDefault: boolean;
  rates: RateEntry[];
}

export interface InvoiceLineItem {
  usageType: string;
  description: string;
  quantity: number;
  unitPrice: number;
  amount: number;
  includedQuantity: number;
  overageQuantity: number;
}

export interface Invoice {
  invoiceId: string;
  tenantId: string;
  periodStart: string;
  periodEnd: string;
  currency: string;
  lineItems: InvoiceLineItem[];
  subtotal: number;
  tax: number;
  total: number;
  status: string;
  generatedAt: string;
  issuedAt: string | null;
  paidAt: string | null;
}

export interface GenerateInvoiceInput {
  periodStart: string;
  periodEnd: string;
}

export interface UsageSummary {
  usageType: string;
  totalQuantity: number;
  recordCount: number;
  periodStart: string;
  periodEnd: string;
  lastUpdatedAt: string;
}

export interface UsageRecord {
  recordId: string;
  usageType: string;
  quantity: number;
  unit: string;
  channel: string | null;
  referenceId: string | null;
  recordedAt: string;
}

export interface Quota {
  maxConcurrentChannels: number;
  maxActiveCampaigns: number;
  maxMonthlyVoiceMinutes: number | null;
  maxMonthlyMessages: number | null;
  maxStorageBytes: number | null;
  maxActiveAgents: number | null;
  quotaAction: string;
}

export interface QuotaStatus {
  tenantId: string;
  quota: Quota | null;
  currentUsage: UsageSummary[];
}

export interface UpdateQuotaInput {
  maxConcurrentChannels?: number;
  maxActiveCampaigns?: number;
  maxMonthlyVoiceMinutes?: number | null;
  maxMonthlyMessages?: number | null;
  maxStorageBytes?: number | null;
  maxActiveAgents?: number | null;
  quotaAction?: string;
}

export const USAGE_TYPES = [
  'VoiceInbound', 'VoiceOutbound', 'SmsInbound', 'SmsOutbound',
  'WhatsAppInbound', 'WhatsAppOutbound', 'EmailInbound', 'EmailOutbound',
  'WebChatSession', 'TelegramInbound', 'TelegramOutbound',
  'RecordingStorage', 'MediaStorage', 'DialerAttempt', 'DialerConnected',
  'AgentLoginHour', 'AiAnalysis',
] as const;

export const QUOTA_ACTIONS = ['Warn', 'SoftBlock', 'HardBlock'] as const;

export const INVOICE_STATUSES = ['Draft', 'Issued', 'Paid', 'Void'] as const;

// --- Helper ---

function useBillingTenantId(): string {
  const active = useTenantStore((s) => s.activeTenantId);
  const auth = useAuthStore((s) => s.tenantId);
  return active ?? auth ?? '';
}

// --- Rate Cards ---

export function useRateCards() {
  const tenantId = useBillingTenantId();
  return useQuery({
    queryKey: ['rate-cards', tenantId],
    queryFn: () =>
      customFetch<RateCard[]>({
        url: '/api/management/rate-cards',
        method: 'GET',
        params: { tenantId },
      }),
    enabled: !!tenantId,
  });
}

export function useCreateRateCard() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: (data: CreateRateCardInput) =>
      customFetch<RateCard>({
        url: '/api/management/rate-cards',
        method: 'POST',
        params: { tenantId },
        data,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['rate-cards', tenantId] });
      toast.success('Rate card created');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useUpdateRateCard() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: ({ id, ...data }: { id: string } & CreateRateCardInput) =>
      customFetch<RateCard>({
        url: `/api/management/rate-cards/${id}`,
        method: 'PUT',
        params: { tenantId },
        data,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['rate-cards', tenantId] });
      toast.success('Rate card updated');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useDeleteRateCard() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: (id: string) =>
      customFetch<void>({
        url: `/api/management/rate-cards/${id}`,
        method: 'DELETE',
        params: { tenantId },
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['rate-cards', tenantId] });
      toast.success('Rate card deleted');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

// --- Invoices ---

export function useInvoices(page = 1, pageSize = 20) {
  const tenantId = useBillingTenantId();
  return useQuery({
    queryKey: ['invoices', tenantId, page, pageSize],
    queryFn: () =>
      customFetch<Invoice[]>({
        url: '/api/management/invoices',
        method: 'GET',
        params: { tenantId, page: String(page), pageSize: String(pageSize) },
      }),
    enabled: !!tenantId,
    placeholderData: (prev) => prev,
  });
}

export function useGenerateInvoice() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: (data: GenerateInvoiceInput) =>
      customFetch<Invoice>({
        url: '/api/management/invoices/generate',
        method: 'POST',
        params: { tenantId },
        data,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['invoices', tenantId] });
      toast.success('Invoice generated');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

export function useInvoice(id: string) {
  const tenantId = useBillingTenantId();
  return useQuery({
    queryKey: ['invoice', tenantId, id],
    queryFn: () =>
      customFetch<Invoice>({
        url: `/api/management/invoices/${id}`,
        method: 'GET',
        params: { tenantId },
      }),
    enabled: !!tenantId && !!id,
  });
}

export function useIssueInvoice() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: (id: string) =>
      customFetch<{ invoiceId: string; status: string }>({
        url: `/api/management/invoices/${id}/issue`,
        method: 'POST',
        params: { tenantId },
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['invoices', tenantId] });
      toast.success('Invoice issued');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}

// --- Usage ---

export function useUsageSummary(from?: string, until?: string) {
  const tenantId = useBillingTenantId();
  const params: Record<string, string> = {};
  if (from) params.from = from;
  if (until) params.until = until;
  return useQuery({
    queryKey: ['usage-summary', tenantId, from, until],
    queryFn: () =>
      customFetch<UsageSummary[]>({
        url: `/api/management/tenants/${tenantId}/usage`,
        method: 'GET',
        params,
      }),
    enabled: !!tenantId,
  });
}

export function useUsageDetails(opts: {
  from?: string;
  until?: string;
  type?: string;
  page?: number;
  pageSize?: number;
} = {}) {
  const tenantId = useBillingTenantId();
  const params: Record<string, string> = {};
  if (opts.from) params.from = opts.from;
  if (opts.until) params.until = opts.until;
  if (opts.type) params.type = opts.type;
  if (opts.page) params.page = String(opts.page);
  if (opts.pageSize) params.pageSize = String(opts.pageSize);
  return useQuery({
    queryKey: ['usage-details', tenantId, opts],
    queryFn: () =>
      customFetch<UsageRecord[]>({
        url: `/api/management/tenants/${tenantId}/usage/details`,
        method: 'GET',
        params,
      }),
    enabled: !!tenantId,
    placeholderData: (prev) => prev,
  });
}

// --- Quotas ---

export function useQuotaStatus() {
  const tenantId = useBillingTenantId();
  return useQuery({
    queryKey: ['quota-status', tenantId],
    queryFn: () =>
      customFetch<QuotaStatus>({
        url: `/api/management/tenants/${tenantId}/quota`,
        method: 'GET',
      }),
    enabled: !!tenantId,
  });
}

export function useUpdateQuota() {
  const qc = useQueryClient();
  const tenantId = useBillingTenantId();
  return useMutation({
    mutationFn: (data: UpdateQuotaInput) =>
      customFetch<Quota>({
        url: `/api/management/tenants/${tenantId}/quota`,
        method: 'PUT',
        data,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['quota-status', tenantId] });
      toast.success('Quota updated');
    },
    onError: (err: Error) => toast.error(err.message),
  });
}
```

- [ ] **Step 2: Verify TypeScript compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors related to `use-billing.ts`

- [ ] **Step 3: Commit**

```bash
git add src/core/api/hooks/use-billing.ts
git commit -m "feat(billing): add TanStack Query hooks and types for billing management API"
```

---

### Task 2: Routing + Sidebar Wiring

**Files:**
- Modify: `src/router.tsx`
- Modify: `src/admin/sidebar.tsx`

Wire up the 5 billing routes and sidebar navigation so pages can be tested as they're built. Pages start as placeholder stubs, replaced in subsequent tasks.

- [ ] **Step 1: Add lazy imports and routes to router.tsx**

Add these lazy imports after the existing lazy imports (after line ~51 `const AuthSessionsPage = lazy(...)`)

```typescript
const RateCardsPage = lazy(() => import('@/admin/billing/rate-cards-page'));
const InvoicesPage = lazy(() => import('@/admin/billing/invoices-page'));
const UsagePage = lazy(() => import('@/admin/billing/usage-page'));
const QuotasPage = lazy(() => import('@/admin/billing/quotas-page'));
```

Add these routes inside the `admin` children array, after the `auth-sessions` route (after line ~508):

```typescript
          {
            path: 'billing/rate-cards',
            element: (
              <PermissionGuard requires="system:tenant:configure" redirect>
                <LazyLoad>
                  <RateCardsPage />
                </LazyLoad>
              </PermissionGuard>
            ),
          },
          {
            path: 'billing/invoices',
            element: (
              <PermissionGuard requires="system:tenant:configure" redirect>
                <LazyLoad>
                  <InvoicesPage />
                </LazyLoad>
              </PermissionGuard>
            ),
          },
          {
            path: 'billing/usage',
            element: (
              <PermissionGuard requires="system:tenant:configure" redirect>
                <LazyLoad>
                  <UsagePage />
                </LazyLoad>
              </PermissionGuard>
            ),
          },
          {
            path: 'billing/quotas',
            element: (
              <PermissionGuard requires="system:tenant:configure" redirect>
                <LazyLoad>
                  <QuotasPage />
                </LazyLoad>
              </PermissionGuard>
            ),
          },
```

- [ ] **Step 2: Add billing group to sidebar.tsx**

Add import for new icons at the top (merge into existing lucide-react import):

```typescript
import {
  // ... existing imports ...,
  CreditCard,
  Receipt,
  BarChart3,
  Gauge,
} from 'lucide-react';
```

Add billing group to the `groups` array, after the `compliance` group and before `ai-automation`:

```typescript
  {
    key: 'billing',
    labelKey: 'admin:sidebar.billing',
    requiredPermission: 'system:tenant:configure',
    items: [
      { key: 'rate-cards', labelKey: 'admin:sidebar.rateCards', to: '/admin/billing/rate-cards', icon: CreditCard, requiredPermission: 'system:tenant:configure' },
      { key: 'invoices', labelKey: 'admin:sidebar.invoices', to: '/admin/billing/invoices', icon: Receipt, requiredPermission: 'system:tenant:configure' },
      { key: 'usage', labelKey: 'admin:sidebar.usage', to: '/admin/billing/usage', icon: BarChart3, requiredPermission: 'system:tenant:configure' },
      { key: 'quotas', labelKey: 'admin:sidebar.quotas', to: '/admin/billing/quotas', icon: Gauge, requiredPermission: 'system:tenant:configure' },
    ],
  },
```

- [ ] **Step 3: Create placeholder pages**

Create 4 placeholder files so routes resolve. Each one will be replaced in Tasks 3-6.

`src/admin/billing/rate-cards-page.tsx`:
```tsx
export default function RateCardsPage() {
  return <div className="p-6 text-sm text-slate-400">Rate Cards — loading...</div>;
}
```

`src/admin/billing/invoices-page.tsx`:
```tsx
export default function InvoicesPage() {
  return <div className="p-6 text-sm text-slate-400">Invoices — loading...</div>;
}
```

`src/admin/billing/usage-page.tsx`:
```tsx
export default function UsagePage() {
  return <div className="p-6 text-sm text-slate-400">Usage — loading...</div>;
}
```

`src/admin/billing/quotas-page.tsx`:
```tsx
export default function QuotasPage() {
  return <div className="p-6 text-sm text-slate-400">Quotas — loading...</div>;
}
```

- [ ] **Step 4: Verify build compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors

- [ ] **Step 5: Commit**

```bash
git add src/router.tsx src/admin/sidebar.tsx src/admin/billing/
git commit -m "feat(billing): add billing routes, sidebar group, and placeholder pages"
```

---

### Task 3: Rate Cards Page + Form

**Files:**
- Create: `src/admin/billing/rate-card-form.tsx`
- Modify: `src/admin/billing/rate-cards-page.tsx` (replace placeholder)

Rate card CRUD: list table with search, create/edit Sheet form with `useFieldArray` for rate entries, delete with 3s confirmation.

- [ ] **Step 1: Create the rate card form component**

```tsx
// src/admin/billing/rate-card-form.tsx
import { useEffect, useCallback } from 'react';
import { useForm, Controller, useFieldArray } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Plus, Trash2 } from 'lucide-react';
import { Button } from '@/core/ui/button';
import { Input } from '@/core/ui/input';
import { Label } from '@/core/ui/label';
import { Switch } from '@/core/ui/switch';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/core/ui/select';
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
  SheetFooter,
} from '@/core/ui/sheet';
import {
  useCreateRateCard,
  useUpdateRateCard,
  USAGE_TYPES,
  type RateCard,
  type CreateRateCardInput,
} from '@/core/api/hooks/use-billing';

const rateEntrySchema = z.object({
  usageType: z.string().min(1, 'Required'),
  unitPrice: z.coerce.number().min(0),
  includedQuantity: z.coerce.number().min(0),
});

const rateCardSchema = z.object({
  name: z.string().min(1, 'Name is required'),
  currency: z.string().min(1, 'Currency is required'),
  effectiveFrom: z.string().min(1, 'Start date required'),
  effectiveTo: z.string().optional(),
  isDefault: z.boolean(),
  rates: z.array(rateEntrySchema).min(1, 'At least one rate entry is required'),
});

type RateCardFormValues = z.infer<typeof rateCardSchema>;

interface RateCardFormProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mode: 'create' | 'edit';
  rateCard?: RateCard;
}

function mapToForm(rc: RateCard): RateCardFormValues {
  return {
    name: rc.name,
    currency: rc.currency,
    effectiveFrom: rc.effectiveFrom.slice(0, 16),
    effectiveTo: rc.effectiveTo?.slice(0, 16) ?? '',
    isDefault: rc.isDefault,
    rates: rc.rates.map((r) => ({
      usageType: r.usageType,
      unitPrice: r.unitPrice,
      includedQuantity: r.includedQuantity,
    })),
  };
}

const DEFAULT_VALUES: RateCardFormValues = {
  name: '',
  currency: 'USD',
  effectiveFrom: '',
  effectiveTo: '',
  isDefault: false,
  rates: [],
};

export function RateCardForm({ open, onOpenChange, mode, rateCard }: RateCardFormProps) {
  const createRateCard = useCreateRateCard();
  const updateRateCard = useUpdateRateCard();

  const {
    register,
    handleSubmit,
    control,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<RateCardFormValues>({
    resolver: zodResolver(rateCardSchema) as any,
    defaultValues: DEFAULT_VALUES,
  });

  const { fields, append, remove } = useFieldArray({ control, name: 'rates' });

  useEffect(() => {
    if (open) {
      reset(rateCard ? mapToForm(rateCard) : DEFAULT_VALUES);
    }
  }, [open, rateCard, reset]);

  const addRate = useCallback(() => {
    append({ usageType: 'VoiceInbound', unitPrice: 0, includedQuantity: 0 });
  }, [append]);

  const onSubmit = handleSubmit((values) => {
    const payload: CreateRateCardInput = {
      name: values.name,
      currency: values.currency,
      effectiveFrom: new Date(values.effectiveFrom).toISOString(),
      effectiveTo: values.effectiveTo ? new Date(values.effectiveTo).toISOString() : null,
      isDefault: values.isDefault,
      rates: values.rates.map((r) => ({
        usageType: r.usageType,
        unitPrice: r.unitPrice,
        includedQuantity: r.includedQuantity,
        tiers: null,
      })),
    };

    if (mode === 'edit' && rateCard) {
      updateRateCard.mutate({ id: rateCard.rateCardId, ...payload });
    } else {
      createRateCard.mutate(payload);
    }
    onOpenChange(false);
  });

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="sm:max-w-lg">
        <SheetHeader>
          <SheetTitle>{mode === 'create' ? 'Create rate card' : 'Edit rate card'}</SheetTitle>
          <SheetDescription>
            {mode === 'create'
              ? 'Define pricing rates for usage types.'
              : 'Update rate card configuration.'}
          </SheetDescription>
        </SheetHeader>

        <form onSubmit={onSubmit} className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {/* Name */}
          <div className="space-y-1.5">
            <Label htmlFor="rc-name">Name</Label>
            <Input id="rc-name" data-testid="rate-card-name" placeholder="Standard pricing" {...register('name')} />
            {errors.name && <p className="text-xs text-destructive">{errors.name.message}</p>}
          </div>

          {/* Currency */}
          <div className="space-y-1.5">
            <Label htmlFor="rc-currency">Currency</Label>
            <Input id="rc-currency" data-testid="rate-card-currency" placeholder="USD" {...register('currency')} />
            {errors.currency && <p className="text-xs text-destructive">{errors.currency.message}</p>}
          </div>

          {/* Dates */}
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="rc-from">Effective from</Label>
              <Input id="rc-from" type="datetime-local" data-testid="rate-card-from" {...register('effectiveFrom')} />
              {errors.effectiveFrom && <p className="text-xs text-destructive">{errors.effectiveFrom.message}</p>}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="rc-to">Effective to</Label>
              <Input id="rc-to" type="datetime-local" data-testid="rate-card-to" {...register('effectiveTo')} />
            </div>
          </div>

          {/* Default */}
          <div className="flex items-center gap-3">
            <Controller
              name="isDefault"
              control={control}
              render={({ field }) => (
                <Switch id="rc-default" checked={field.value} onCheckedChange={field.onChange} />
              )}
            />
            <Label htmlFor="rc-default">Default rate card</Label>
          </div>

          {/* Rate Entries */}
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <Label>Rate entries</Label>
              <Button type="button" size="sm" variant="outline" onClick={addRate} data-testid="add-rate-entry">
                <Plus className="mr-1 h-3.5 w-3.5" />
                Add rate
              </Button>
            </div>

            {errors.rates?.root && (
              <p className="text-xs text-destructive">{errors.rates.root.message}</p>
            )}

            {fields.length === 0 && (
              <p className="text-sm text-muted-foreground">No rate entries yet. Add at least one.</p>
            )}

            {fields.map((field, index) => (
              <div key={field.id} className="rounded-md border bg-muted/30 p-3 space-y-2" data-testid={`rate-entry-${index}`}>
                <div className="flex items-center justify-between">
                  <span className="text-xs font-medium text-muted-foreground">Rate #{index + 1}</span>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    className="h-6 w-6 p-0 text-destructive hover:text-destructive"
                    onClick={() => remove(index)}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>

                {/* Usage Type */}
                <Controller
                  name={`rates.${index}.usageType`}
                  control={control}
                  render={({ field: f }) => (
                    <Select value={f.value} onValueChange={f.onChange}>
                      <SelectTrigger className="w-full">
                        <SelectValue placeholder="Select usage type" />
                      </SelectTrigger>
                      <SelectContent>
                        {USAGE_TYPES.map((ut) => (
                          <SelectItem key={ut} value={ut}>{ut}</SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  )}
                />

                {/* Price + Included */}
                <div className="grid grid-cols-2 gap-2">
                  <div className="space-y-1">
                    <Label className="text-xs">Unit price</Label>
                    <Input
                      type="number"
                      step="0.0001"
                      placeholder="0.00"
                      {...register(`rates.${index}.unitPrice`, { valueAsNumber: true })}
                    />
                  </div>
                  <div className="space-y-1">
                    <Label className="text-xs">Included qty</Label>
                    <Input
                      type="number"
                      step="1"
                      placeholder="0"
                      {...register(`rates.${index}.includedQuantity`, { valueAsNumber: true })}
                    />
                  </div>
                </div>
              </div>
            ))}
          </div>

          <SheetFooter className="mt-auto px-0">
            <Button type="submit" disabled={isSubmitting} data-testid="rate-card-submit">
              {mode === 'create' ? 'Create' : 'Save'}
            </Button>
          </SheetFooter>
        </form>
      </SheetContent>
    </Sheet>
  );
}
```

- [ ] **Step 2: Replace rate cards page placeholder with full CRUD page**

```tsx
// src/admin/billing/rate-cards-page.tsx
import { useState, useMemo } from 'react';
import { createColumnHelper } from '@tanstack/react-table';
import { format } from 'date-fns';
import { Plus, Pencil, Trash2 } from 'lucide-react';
import { PageHeader } from '@/admin/shared/page-header';
import { DataTable } from '@/admin/shared/data-table';
import { Button } from '@/core/ui/button';
import { Badge } from '@/core/ui/badge';
import { ConfirmDeleteDialog } from '@/core/ui/confirm-delete-dialog';
import { RateCardForm } from './rate-card-form';
import {
  useRateCards,
  useDeleteRateCard,
  type RateCard,
} from '@/core/api/hooks/use-billing';
import { useTenantStore } from '@/core/tenant/tenant-store';

const col = createColumnHelper<RateCard>();

export default function RateCardsPage() {
  const tenantId = useTenantStore((s) => s.activeTenantId);
  const { data: rateCards = [] } = useRateCards();
  const deleteRateCard = useDeleteRateCard();

  const [createOpen, setCreateOpen] = useState(false);
  const [editCard, setEditCard] = useState<RateCard | undefined>();
  const [deleteCard, setDeleteCard] = useState<RateCard | undefined>();

  const columns = useMemo(
    () => [
      col.accessor('name', {
        header: () => 'Name',
        cell: (info) => <span className="font-medium">{info.getValue()}</span>,
      }),
      col.accessor('currency', {
        header: () => 'Currency',
        cell: (info) => info.getValue(),
      }),
      col.accessor('isDefault', {
        header: () => 'Default',
        cell: (info) =>
          info.getValue() ? (
            <Badge variant="default">Default</Badge>
          ) : null,
      }),
      col.accessor('effectiveFrom', {
        header: () => 'Effective from',
        cell: (info) => format(new Date(info.getValue()), 'MMM d, yyyy'),
      }),
      col.accessor('effectiveTo', {
        header: () => 'Effective to',
        cell: (info) =>
          info.getValue() ? format(new Date(info.getValue()!), 'MMM d, yyyy') : '—',
      }),
      col.accessor('rates', {
        header: () => 'Rates',
        cell: (info) => (
          <span className="text-muted-foreground">{info.getValue().length} entries</span>
        ),
      }),
      col.display({
        id: 'actions',
        cell: ({ row }) => (
          <div className="flex gap-1">
            <Button
              variant="ghost"
              size="sm"
              className="h-7 w-7 p-0"
              data-testid={`edit-rate-card-${row.original.rateCardId}`}
              onClick={(e) => {
                e.stopPropagation();
                setEditCard(row.original);
              }}
            >
              <Pencil className="h-3.5 w-3.5" />
            </Button>
            <Button
              variant="ghost"
              size="sm"
              className="h-7 w-7 p-0 text-destructive hover:text-destructive"
              data-testid={`delete-rate-card-${row.original.rateCardId}`}
              onClick={(e) => {
                e.stopPropagation();
                setDeleteCard(row.original);
              }}
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          </div>
        ),
      }),
    ],
    [],
  );

  if (!tenantId) {
    return (
      <div className="flex h-64 items-center justify-center">
        <p className="text-sm text-muted-foreground" data-testid="no-tenant-message">
          Select a tenant from the <a href="/admin/tenants" className="text-brand underline">Tenants page</a> to manage rate cards.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6" data-testid="rate-cards-page">
      <PageHeader title="Rate Cards" description="Manage pricing rate cards for this tenant.">
        <Button onClick={() => setCreateOpen(true)} data-testid="create-rate-card">
          <Plus className="mr-1.5 h-4 w-4" />
          New rate card
        </Button>
      </PageHeader>

      <DataTable
        data={rateCards}
        columns={columns}
        searchPlaceholder="Search rate cards..."
      />

      <RateCardForm
        open={createOpen}
        onOpenChange={setCreateOpen}
        mode="create"
      />

      <RateCardForm
        open={!!editCard}
        onOpenChange={(open) => { if (!open) setEditCard(undefined); }}
        mode="edit"
        rateCard={editCard}
      />

      <ConfirmDeleteDialog
        open={!!deleteCard}
        onOpenChange={(open) => { if (!open) setDeleteCard(undefined); }}
        onConfirm={() => {
          if (deleteCard) deleteRateCard.mutate(deleteCard.rateCardId);
          setDeleteCard(undefined);
        }}
        entityName={deleteCard?.name ?? ''}
        entityType="rate card"
        isPending={deleteRateCard.isPending}
      />
    </div>
  );
}
```

- [ ] **Step 3: Verify build compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
git add src/admin/billing/rate-cards-page.tsx src/admin/billing/rate-card-form.tsx
git commit -m "feat(billing): add rate cards page with CRUD and field array form"
```

---

### Task 4: Invoices Page

**Files:**
- Modify: `src/admin/billing/invoices-page.tsx` (replace placeholder)

Invoice list with pagination, generate dialog, detail view in sheet, and issue action button.

- [ ] **Step 1: Replace invoices page placeholder with full implementation**

```tsx
// src/admin/billing/invoices-page.tsx
import { useState, useMemo } from 'react';
import { createColumnHelper } from '@tanstack/react-table';
import { format } from 'date-fns';
import { FileText, Send, Eye } from 'lucide-react';
import { PageHeader } from '@/admin/shared/page-header';
import { DataTable } from '@/admin/shared/data-table';
import { Button } from '@/core/ui/button';
import { Badge } from '@/core/ui/badge';
import { Input } from '@/core/ui/input';
import { Label } from '@/core/ui/label';
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from '@/core/ui/sheet';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from '@/core/ui/dialog';
import {
  useInvoices,
  useGenerateInvoice,
  useIssueInvoice,
  type Invoice,
} from '@/core/api/hooks/use-billing';
import { useTenantStore } from '@/core/tenant/tenant-store';

const col = createColumnHelper<Invoice>();

const STATUS_COLORS: Record<string, 'default' | 'secondary' | 'destructive' | 'outline'> = {
  Draft: 'secondary',
  Issued: 'default',
  Paid: 'default',
  Void: 'destructive',
};

function formatCurrency(amount: number, currency: string) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(amount);
}

export default function InvoicesPage() {
  const tenantId = useTenantStore((s) => s.activeTenantId);
  const [page, setPage] = useState(1);
  const { data: invoices = [], isFetching } = useInvoices(page, 20);
  const generateInvoice = useGenerateInvoice();
  const issueInvoice = useIssueInvoice();

  const [generateOpen, setGenerateOpen] = useState(false);
  const [periodStart, setPeriodStart] = useState('');
  const [periodEnd, setPeriodEnd] = useState('');
  const [detailInvoice, setDetailInvoice] = useState<Invoice | undefined>();

  const columns = useMemo(
    () => [
      col.accessor('invoiceId', {
        header: () => 'Invoice',
        cell: (info) => (
          <span className="font-mono text-xs">{info.getValue().slice(0, 8)}...</span>
        ),
      }),
      col.accessor('periodStart', {
        header: () => 'Period',
        cell: (info) => {
          const inv = info.row.original;
          return `${format(new Date(inv.periodStart), 'MMM d')} — ${format(new Date(inv.periodEnd), 'MMM d, yyyy')}`;
        },
      }),
      col.accessor('total', {
        header: () => 'Total',
        cell: (info) => (
          <span className="font-medium">
            {formatCurrency(info.getValue(), info.row.original.currency)}
          </span>
        ),
      }),
      col.accessor('status', {
        header: () => 'Status',
        cell: (info) => (
          <Badge variant={STATUS_COLORS[info.getValue()] ?? 'outline'}>
            {info.getValue()}
          </Badge>
        ),
      }),
      col.accessor('generatedAt', {
        header: () => 'Generated',
        cell: (info) => format(new Date(info.getValue()), 'MMM d, yyyy HH:mm'),
      }),
      col.display({
        id: 'actions',
        cell: ({ row }) => (
          <div className="flex gap-1">
            <Button
              variant="ghost"
              size="sm"
              className="h-7 w-7 p-0"
              data-testid={`view-invoice-${row.original.invoiceId}`}
              onClick={(e) => {
                e.stopPropagation();
                setDetailInvoice(row.original);
              }}
            >
              <Eye className="h-3.5 w-3.5" />
            </Button>
            {row.original.status === 'Draft' && (
              <Button
                variant="ghost"
                size="sm"
                className="h-7 w-7 p-0 text-brand hover:text-brand"
                data-testid={`issue-invoice-${row.original.invoiceId}`}
                onClick={(e) => {
                  e.stopPropagation();
                  issueInvoice.mutate(row.original.invoiceId);
                }}
              >
                <Send className="h-3.5 w-3.5" />
              </Button>
            )}
          </div>
        ),
      }),
    ],
    [issueInvoice],
  );

  const handleGenerate = () => {
    if (!periodStart || !periodEnd) return;
    generateInvoice.mutate({
      periodStart: new Date(periodStart).toISOString(),
      periodEnd: new Date(periodEnd).toISOString(),
    });
    setGenerateOpen(false);
    setPeriodStart('');
    setPeriodEnd('');
  };

  if (!tenantId) {
    return (
      <div className="flex h-64 items-center justify-center">
        <p className="text-sm text-muted-foreground" data-testid="no-tenant-message">
          Select a tenant from the <a href="/admin/tenants" className="text-brand underline">Tenants page</a> to manage invoices.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6" data-testid="invoices-page">
      <PageHeader title="Invoices" description="Generate and manage billing invoices.">
        <Button onClick={() => setGenerateOpen(true)} data-testid="generate-invoice">
          <FileText className="mr-1.5 h-4 w-4" />
          Generate invoice
        </Button>
      </PageHeader>

      <DataTable
        data={invoices}
        columns={columns}
        searchPlaceholder="Search invoices..."
      />

      {/* Generate Invoice Dialog */}
      <Dialog open={generateOpen} onOpenChange={setGenerateOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Generate invoice</DialogTitle>
            <DialogDescription>
              Select the billing period to generate an invoice.
            </DialogDescription>
          </DialogHeader>
          <div className="grid grid-cols-2 gap-3 py-4">
            <div className="space-y-1.5">
              <Label htmlFor="gen-start">Period start</Label>
              <Input
                id="gen-start"
                type="datetime-local"
                data-testid="generate-period-start"
                value={periodStart}
                onChange={(e) => setPeriodStart(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="gen-end">Period end</Label>
              <Input
                id="gen-end"
                type="datetime-local"
                data-testid="generate-period-end"
                value={periodEnd}
                onChange={(e) => setPeriodEnd(e.target.value)}
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setGenerateOpen(false)}>Cancel</Button>
            <Button
              onClick={handleGenerate}
              disabled={!periodStart || !periodEnd || generateInvoice.isPending}
              data-testid="generate-invoice-submit"
            >
              {generateInvoice.isPending ? 'Generating...' : 'Generate'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Invoice Detail Sheet */}
      <Sheet open={!!detailInvoice} onOpenChange={(open) => { if (!open) setDetailInvoice(undefined); }}>
        <SheetContent side="right" className="sm:max-w-lg">
          <SheetHeader>
            <SheetTitle>Invoice detail</SheetTitle>
            <SheetDescription>
              {detailInvoice && `${format(new Date(detailInvoice.periodStart), 'MMM d')} — ${format(new Date(detailInvoice.periodEnd), 'MMM d, yyyy')}`}
            </SheetDescription>
          </SheetHeader>

          {detailInvoice && (
            <div className="space-y-4 px-4" data-testid="invoice-detail">
              {/* Summary */}
              <div className="grid grid-cols-3 gap-3">
                <div>
                  <p className="text-xs text-muted-foreground">Subtotal</p>
                  <p className="text-sm font-medium">{formatCurrency(detailInvoice.subtotal, detailInvoice.currency)}</p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">Tax</p>
                  <p className="text-sm font-medium">{formatCurrency(detailInvoice.tax, detailInvoice.currency)}</p>
                </div>
                <div>
                  <p className="text-xs text-muted-foreground">Total</p>
                  <p className="text-sm font-semibold">{formatCurrency(detailInvoice.total, detailInvoice.currency)}</p>
                </div>
              </div>

              <div className="flex items-center gap-2">
                <Badge variant={STATUS_COLORS[detailInvoice.status] ?? 'outline'}>
                  {detailInvoice.status}
                </Badge>
                {detailInvoice.issuedAt && (
                  <span className="text-xs text-muted-foreground">
                    Issued {format(new Date(detailInvoice.issuedAt), 'MMM d, yyyy')}
                  </span>
                )}
              </div>

              {/* Line Items */}
              <div className="space-y-2">
                <p className="text-sm font-medium">Line items</p>
                <div className="space-y-1">
                  {detailInvoice.lineItems.map((li, idx) => (
                    <div
                      key={idx}
                      className="flex items-center justify-between rounded-md border px-3 py-2 text-sm"
                      data-testid={`line-item-${idx}`}
                    >
                      <div>
                        <p className="font-medium">{li.description}</p>
                        <p className="text-xs text-muted-foreground">
                          {li.usageType} · {li.quantity} units @ {formatCurrency(li.unitPrice, detailInvoice.currency)}
                        </p>
                      </div>
                      <span className="font-medium">{formatCurrency(li.amount, detailInvoice.currency)}</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )}
        </SheetContent>
      </Sheet>
    </div>
  );
}
```

- [ ] **Step 2: Verify build compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add src/admin/billing/invoices-page.tsx
git commit -m "feat(billing): add invoices page with generate, detail view, and issue action"
```

---

### Task 5: Usage Dashboard Page

**Files:**
- Modify: `src/admin/billing/usage-page.tsx` (replace placeholder)

Usage summary cards with Recharts bar chart, detailed records table with date range and type filters.

- [ ] **Step 1: Replace usage page placeholder with full implementation**

```tsx
// src/admin/billing/usage-page.tsx
import { useState, useMemo } from 'react';
import { createColumnHelper } from '@tanstack/react-table';
import { format, startOfMonth } from 'date-fns';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from 'recharts';
import { Activity } from 'lucide-react';
import { PageHeader } from '@/admin/shared/page-header';
import { DataTable } from '@/admin/shared/data-table';
import { Button } from '@/core/ui/button';
import { Input } from '@/core/ui/input';
import { Label } from '@/core/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/core/ui/select';
import {
  useUsageSummary,
  useUsageDetails,
  USAGE_TYPES,
  type UsageRecord,
} from '@/core/api/hooks/use-billing';
import { useTenantStore } from '@/core/tenant/tenant-store';

const col = createColumnHelper<UsageRecord>();

export default function UsagePage() {
  const tenantId = useTenantStore((s) => s.activeTenantId);

  const now = new Date();
  const [from, setFrom] = useState(format(startOfMonth(now), "yyyy-MM-dd'T'HH:mm"));
  const [until, setUntil] = useState(format(now, "yyyy-MM-dd'T'HH:mm"));
  const [typeFilter, setTypeFilter] = useState('all');
  const [detailPage, setDetailPage] = useState(1);

  const fromISO = from ? new Date(from).toISOString() : undefined;
  const untilISO = until ? new Date(until).toISOString() : undefined;

  const { data: summaries = [] } = useUsageSummary(fromISO, untilISO);
  const { data: records = [], isFetching } = useUsageDetails({
    from: fromISO,
    until: untilISO,
    type: typeFilter !== 'all' ? typeFilter : undefined,
    page: detailPage,
    pageSize: 50,
  });

  const chartData = useMemo(
    () =>
      summaries.map((s) => ({
        name: s.usageType.replace(/([A-Z])/g, ' $1').trim(),
        quantity: s.totalQuantity,
        records: s.recordCount,
      })),
    [summaries],
  );

  const columns = useMemo(
    () => [
      col.accessor('recordedAt', {
        header: () => 'Time',
        cell: (info) => format(new Date(info.getValue()), 'MMM d, HH:mm:ss'),
      }),
      col.accessor('usageType', {
        header: () => 'Type',
        cell: (info) => info.getValue(),
      }),
      col.accessor('quantity', {
        header: () => 'Quantity',
        cell: (info) => info.getValue().toLocaleString(),
      }),
      col.accessor('unit', {
        header: () => 'Unit',
        cell: (info) => info.getValue(),
      }),
      col.accessor('channel', {
        header: () => 'Channel',
        cell: (info) => info.getValue() ?? '—',
      }),
      col.accessor('referenceId', {
        header: () => 'Reference',
        cell: (info) =>
          info.getValue() ? (
            <span className="font-mono text-xs">{info.getValue()!.slice(0, 12)}...</span>
          ) : (
            '—'
          ),
      }),
    ],
    [],
  );

  if (!tenantId) {
    return (
      <div className="flex h-64 items-center justify-center">
        <p className="text-sm text-muted-foreground" data-testid="no-tenant-message">
          Select a tenant from the <a href="/admin/tenants" className="text-brand underline">Tenants page</a> to view usage.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6" data-testid="usage-page">
      <PageHeader title="Usage" description="View metered usage summary and detailed records." />

      {/* Filters */}
      <div className="flex flex-wrap items-end gap-3 rounded-md border bg-card p-4" data-testid="usage-filters">
        <div className="space-y-1.5">
          <Label htmlFor="usage-from">From</Label>
          <Input
            id="usage-from"
            type="datetime-local"
            value={from}
            onChange={(e) => { setFrom(e.target.value); setDetailPage(1); }}
            className="w-52"
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="usage-until">Until</Label>
          <Input
            id="usage-until"
            type="datetime-local"
            value={until}
            onChange={(e) => { setUntil(e.target.value); setDetailPage(1); }}
            className="w-52"
          />
        </div>
        <div className="space-y-1.5">
          <Label>Type</Label>
          <Select value={typeFilter} onValueChange={(v) => { setTypeFilter(v); setDetailPage(1); }}>
            <SelectTrigger className="w-48" data-testid="usage-type-filter">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All types</SelectItem>
              {USAGE_TYPES.map((ut) => (
                <SelectItem key={ut} value={ut}>{ut}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>

      {/* Summary Chart */}
      {summaries.length > 0 && (
        <div className="rounded-md border bg-card p-4" data-testid="usage-chart">
          <h3 className="mb-3 text-sm font-medium">Usage by type</h3>
          <ResponsiveContainer width="100%" height={260}>
            <BarChart data={chartData} margin={{ top: 5, right: 20, bottom: 60, left: 20 }}>
              <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
              <XAxis
                dataKey="name"
                tick={{ fontSize: 11 }}
                angle={-45}
                textAnchor="end"
                interval={0}
              />
              <YAxis tick={{ fontSize: 11 }} />
              <Tooltip />
              <Bar dataKey="quantity" fill="var(--color-brand)" radius={[4, 4, 0, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Summary Cards */}
      {summaries.length > 0 && (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4" data-testid="usage-summary-cards">
          {summaries.map((s) => (
            <div key={s.usageType} className="rounded-md border bg-card p-3">
              <p className="text-xs text-muted-foreground">{s.usageType}</p>
              <p className="text-lg font-semibold">{s.totalQuantity.toLocaleString()}</p>
              <p className="text-xs text-muted-foreground">{s.recordCount} records</p>
            </div>
          ))}
        </div>
      )}

      {/* Detailed Records */}
      <div>
        <h3 className="mb-3 text-sm font-medium">Detailed records</h3>
        <DataTable
          data={records}
          columns={columns}
          searchPlaceholder="Search records..."
        />
      </div>
    </div>
  );
}
```

- [ ] **Step 2: Verify build compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add src/admin/billing/usage-page.tsx
git commit -m "feat(billing): add usage dashboard with summary chart, cards, and detailed records"
```

---

### Task 6: Quotas Page

**Files:**
- Modify: `src/admin/billing/quotas-page.tsx` (replace placeholder)

Quota status display with progress bars showing current usage vs limits, plus an edit form for updating quota values.

- [ ] **Step 1: Replace quotas page placeholder with full implementation**

```tsx
// src/admin/billing/quotas-page.tsx
import { useState, useEffect } from 'react';
import { useForm, Controller } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Pencil, ShieldAlert, ShieldCheck, ShieldX } from 'lucide-react';
import { PageHeader } from '@/admin/shared/page-header';
import { Button } from '@/core/ui/button';
import { Badge } from '@/core/ui/badge';
import { Input } from '@/core/ui/input';
import { Label } from '@/core/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/core/ui/select';
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
  SheetFooter,
} from '@/core/ui/sheet';
import {
  useQuotaStatus,
  useUpdateQuota,
  QUOTA_ACTIONS,
  type Quota,
  type UpdateQuotaInput,
} from '@/core/api/hooks/use-billing';
import { useTenantStore } from '@/core/tenant/tenant-store';

const quotaSchema = z.object({
  maxConcurrentChannels: z.coerce.number().int().min(1),
  maxActiveCampaigns: z.coerce.number().int().min(1),
  maxMonthlyVoiceMinutes: z.coerce.number().int().min(0).nullable(),
  maxMonthlyMessages: z.coerce.number().int().min(0).nullable(),
  maxStorageBytes: z.coerce.number().int().min(0).nullable(),
  maxActiveAgents: z.coerce.number().int().min(0).nullable(),
  quotaAction: z.string(),
});

type QuotaFormValues = z.infer<typeof quotaSchema>;

const ACTION_ICONS: Record<string, React.ElementType> = {
  Warn: ShieldAlert,
  SoftBlock: ShieldCheck,
  HardBlock: ShieldX,
};

const ACTION_COLORS: Record<string, string> = {
  Warn: 'text-warning',
  SoftBlock: 'text-brand',
  HardBlock: 'text-destructive',
};

function formatBytes(bytes: number | null): string {
  if (bytes === null || bytes === 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let val = bytes;
  let idx = 0;
  while (val >= 1024 && idx < units.length - 1) {
    val /= 1024;
    idx++;
  }
  return `${val.toFixed(1)} ${units[idx]}`;
}

interface QuotaRowProps {
  label: string;
  limit: number | null;
  usage?: number;
  formatter?: (v: number | null) => string;
}

function QuotaRow({ label, limit, usage = 0, formatter }: QuotaRowProps) {
  const fmt = formatter ?? ((v: number | null) => v?.toLocaleString() ?? '—');
  const pct = limit && limit > 0 ? Math.min(100, (usage / limit) * 100) : 0;
  const color = pct >= 90 ? 'bg-destructive' : pct >= 70 ? 'bg-warning' : 'bg-brand';

  return (
    <div className="space-y-1.5 rounded-md border bg-card p-3">
      <div className="flex items-center justify-between">
        <span className="text-sm font-medium">{label}</span>
        <span className="text-xs text-muted-foreground">
          {usage.toLocaleString()} / {fmt(limit)}
        </span>
      </div>
      {limit !== null && limit > 0 && (
        <div className="h-2 rounded-full bg-muted">
          <div
            className={`h-full rounded-full transition-all ${color}`}
            style={{ width: `${pct}%` }}
          />
        </div>
      )}
    </div>
  );
}

export default function QuotasPage() {
  const tenantId = useTenantStore((s) => s.activeTenantId);
  const { data: status } = useQuotaStatus();
  const updateQuota = useUpdateQuota();
  const [editOpen, setEditOpen] = useState(false);

  const quota = status?.quota;
  const usage = status?.currentUsage ?? [];

  const {
    register,
    handleSubmit,
    control,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<QuotaFormValues>({
    resolver: zodResolver(quotaSchema) as any,
  });

  useEffect(() => {
    if (editOpen && quota) {
      reset({
        maxConcurrentChannels: quota.maxConcurrentChannels,
        maxActiveCampaigns: quota.maxActiveCampaigns,
        maxMonthlyVoiceMinutes: quota.maxMonthlyVoiceMinutes,
        maxMonthlyMessages: quota.maxMonthlyMessages,
        maxStorageBytes: quota.maxStorageBytes,
        maxActiveAgents: quota.maxActiveAgents,
        quotaAction: quota.quotaAction,
      });
    }
  }, [editOpen, quota, reset]);

  const onSubmit = handleSubmit((values) => {
    const data: UpdateQuotaInput = {
      maxConcurrentChannels: values.maxConcurrentChannels,
      maxActiveCampaigns: values.maxActiveCampaigns,
      maxMonthlyVoiceMinutes: values.maxMonthlyVoiceMinutes,
      maxMonthlyMessages: values.maxMonthlyMessages,
      maxStorageBytes: values.maxStorageBytes,
      maxActiveAgents: values.maxActiveAgents,
      quotaAction: values.quotaAction,
    };
    updateQuota.mutate(data);
    setEditOpen(false);
  });

  // Helpers to find usage for a given type pattern
  function usageFor(...types: string[]): number {
    return usage
      .filter((u) => types.some((t) => u.usageType.includes(t)))
      .reduce((sum, u) => sum + u.totalQuantity, 0);
  }

  if (!tenantId) {
    return (
      <div className="flex h-64 items-center justify-center">
        <p className="text-sm text-muted-foreground" data-testid="no-tenant-message">
          Select a tenant from the <a href="/admin/tenants" className="text-brand underline">Tenants page</a> to manage quotas.
        </p>
      </div>
    );
  }

  const ActionIcon = quota ? (ACTION_ICONS[quota.quotaAction] ?? ShieldAlert) : ShieldAlert;

  return (
    <div className="space-y-6" data-testid="quotas-page">
      <PageHeader title="Quotas" description="View and configure tenant usage limits.">
        <Button onClick={() => setEditOpen(true)} data-testid="edit-quota" disabled={!quota}>
          <Pencil className="mr-1.5 h-4 w-4" />
          Edit quotas
        </Button>
      </PageHeader>

      {!quota ? (
        <div className="flex h-40 items-center justify-center rounded-md border bg-card">
          <p className="text-sm text-muted-foreground">No quota configured for this tenant.</p>
        </div>
      ) : (
        <>
          {/* Enforcement Action */}
          <div className="flex items-center gap-2 rounded-md border bg-card p-4" data-testid="quota-action-badge">
            <ActionIcon className={`h-5 w-5 ${ACTION_COLORS[quota.quotaAction] ?? ''}`} />
            <span className="text-sm font-medium">Enforcement:</span>
            <Badge variant={quota.quotaAction === 'HardBlock' ? 'destructive' : 'outline'}>
              {quota.quotaAction}
            </Badge>
          </div>

          {/* Quota Limits with Usage */}
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" data-testid="quota-limits">
            <QuotaRow
              label="Concurrent channels"
              limit={quota.maxConcurrentChannels}
            />
            <QuotaRow
              label="Active campaigns"
              limit={quota.maxActiveCampaigns}
            />
            <QuotaRow
              label="Monthly voice minutes"
              limit={quota.maxMonthlyVoiceMinutes}
              usage={usageFor('Voice')}
            />
            <QuotaRow
              label="Monthly messages"
              limit={quota.maxMonthlyMessages}
              usage={usageFor('Sms', 'WhatsApp', 'Email', 'Telegram', 'WebChat')}
            />
            <QuotaRow
              label="Storage"
              limit={quota.maxStorageBytes}
              usage={usageFor('Storage')}
              formatter={formatBytes}
            />
            <QuotaRow
              label="Active agents"
              limit={quota.maxActiveAgents}
            />
          </div>
        </>
      )}

      {/* Edit Sheet */}
      <Sheet open={editOpen} onOpenChange={setEditOpen}>
        <SheetContent side="right" className="sm:max-w-md">
          <SheetHeader>
            <SheetTitle>Edit quotas</SheetTitle>
            <SheetDescription>Update usage limits for this tenant.</SheetDescription>
          </SheetHeader>

          <form onSubmit={onSubmit} className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
            <div className="space-y-1.5">
              <Label htmlFor="q-channels">Max concurrent channels</Label>
              <Input id="q-channels" type="number" data-testid="quota-channels" {...register('maxConcurrentChannels')} />
              {errors.maxConcurrentChannels && <p className="text-xs text-destructive">{errors.maxConcurrentChannels.message}</p>}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="q-campaigns">Max active campaigns</Label>
              <Input id="q-campaigns" type="number" data-testid="quota-campaigns" {...register('maxActiveCampaigns')} />
              {errors.maxActiveCampaigns && <p className="text-xs text-destructive">{errors.maxActiveCampaigns.message}</p>}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="q-voice">Max monthly voice minutes</Label>
              <Input id="q-voice" type="number" data-testid="quota-voice" {...register('maxMonthlyVoiceMinutes')} />
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="q-messages">Max monthly messages</Label>
              <Input id="q-messages" type="number" data-testid="quota-messages" {...register('maxMonthlyMessages')} />
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="q-storage">Max storage (bytes)</Label>
              <Input id="q-storage" type="number" data-testid="quota-storage" {...register('maxStorageBytes')} />
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="q-agents">Max active agents</Label>
              <Input id="q-agents" type="number" data-testid="quota-agents" {...register('maxActiveAgents')} />
            </div>

            <div className="space-y-1.5">
              <Label>Enforcement action</Label>
              <Controller
                name="quotaAction"
                control={control}
                render={({ field }) => (
                  <Select value={field.value} onValueChange={field.onChange}>
                    <SelectTrigger className="w-full" data-testid="quota-action-select">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {QUOTA_ACTIONS.map((a) => (
                        <SelectItem key={a} value={a}>{a}</SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              />
            </div>

            <SheetFooter className="mt-auto px-0">
              <Button type="submit" disabled={isSubmitting} data-testid="quota-submit">
                {isSubmitting ? 'Saving...' : 'Save'}
              </Button>
            </SheetFooter>
          </form>
        </SheetContent>
      </Sheet>
    </div>
  );
}
```

- [ ] **Step 2: Verify build compiles**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty 2>&1 | head -30`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add src/admin/billing/quotas-page.tsx
git commit -m "feat(billing): add quotas page with status display, progress bars, and edit form"
```

---

### Task 7: Final Verification + Documentation

**Files:**
- Verify: all files compile, lint passes, dev server renders pages
- Modify: `CLAUDE.md` in Platform.Web repo (if needed)

- [ ] **Step 1: Run full type check**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npx tsc --noEmit --pretty`
Expected: 0 errors

- [ ] **Step 2: Run lint**

Run: `cd /media/Data/Source/Verbara/Asterisk.Platform.Web && npm run lint 2>&1 | tail -10`
Expected: No critical errors

- [ ] **Step 3: Verify file count**

Run: `find src/admin/billing -name '*.tsx' -o -name '*.ts' | sort`
Expected: 6 files:
```
src/admin/billing/invoices-page.tsx
src/admin/billing/quotas-page.tsx
src/admin/billing/rate-card-form.tsx
src/admin/billing/rate-cards-page.tsx
src/admin/billing/usage-page.tsx
```
Plus `src/core/api/hooks/use-billing.ts` (1 file)

- [ ] **Step 4: Commit docs if updated**

```bash
git add -A
git commit -m "docs: update CLAUDE.md with billing frontend pages"
```
