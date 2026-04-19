# Sprint 5: Onboarding Wizard + Impersonation Read-Only — Design Spec

**Date:** 2026-04-07
**Decisions:** #7 (Onboarding) + #9 (Impersonation read-only)
**Status:** Approved

---

## Overview

Sprint 5 delivers two capabilities:

1. **Tenant Onboarding** — Automatic provisioning of "Golden Defaults" on tenant creation + optional use-case templates (support/sales/blended) + frontend wizard for first admin + persistent Getting Started checklist.
2. **Impersonation Read-Only** — Permission intersection model that filters to view-only permissions, middleware safety net, audit enhancement with `ImpersonatorId`, and frontend visual differentiation.

---

## Part 1: Tenant Onboarding

### 1.1 TenantProvisioningService

New service that runs automatically when a tenant is created via `ITenantLifecycleHandler.OnTenantCreatedAsync`. Applies Golden Defaults and optional template resources.

**Registration:** Singleton, registered in DI as `ITenantLifecycleHandler` implementation.

**Trigger:** Called by the existing lifecycle dispatch loop in `ManagementTenantEndpoints.CreateTenant` and `PartnerCustomerEndpoints.CreateCustomer`.

### 1.2 Golden Defaults (Always Applied)

These resources are created for EVERY new tenant, regardless of template selection:

| Resource | Details |
|----------|---------|
| **Tenant Roles** | Clone all 8 system role templates via `ITenantRoleStore.CloneFromTemplateAsync` (Agent, Supervisor, QA Analyst, Manager, Admin, System Admin, API, Platform Admin) |
| **Auth Config** | Plan-derived defaults: Starter = basic (MFA optional, 8-char password), Pro = MFA optional + 12-char, Enterprise = MFA required for Admin/Supervisor + 16-char |
| **CSAT Survey** | Default survey: "How would you rate your experience?" (1-5 stars) + "Any additional feedback?" (open text). Type: CSAT. Active. |
| **Retention Policy** | Plan-derived: Starter = 90 days all, Pro = 365 days all, Enterprise = 730 days all |
| **Onboarding Metadata** | `Metadata["OnboardingTemplate"]` = template name or "none" |

### 1.3 Built-In Templates

Three use-case templates define additional resources beyond Golden Defaults:

#### Template: `support`

| Resource | Configuration |
|----------|--------------|
| Queue: "General Support" | SLA: answer 20s, first response 30min, resolution 4h. Hours: Mon-Fri 9-18 (tenant TZ). WrapUp: 30s. |
| Flow: "Support Welcome" | Unpublished. Nodes: SendMessage("Welcome to {tenant} support") → EnqueueNode(General Support) → EndNode |
| Automation: "Auto-Close Inactive" | Trigger: TimerElapsed. Condition: conversation idle > 30 days. Action: close conversation. Priority 100. |
| Automation: "SLA Breach Escalation" | Trigger: SlaBreached. Condition: queue = General Support. Action: set priority = high. Priority 50. |

#### Template: `sales`

| Resource | Configuration |
|----------|--------------|
| Queue: "Sales Inbound" | SLA: answer 15s, first response 10min, resolution 1h. Hours: Mon-Fri 9-18. WrapUp: 30s. |
| Queue: "Sales Outbound" | SLA: answer 15s, first response 10min, resolution 1h. Hours: Mon-Fri 9-18. WrapUp: 30s. |
| Flow: "Sales Greeting" | Unpublished. Nodes: SendMessage("Thanks for reaching out to {tenant} sales") → EnqueueNode(Sales Inbound) → EndNode |
| Automation: "Hot Lead Priority" | Trigger: MessageReceived. Condition: message contains "urgent" or "pricing". Action: set priority = high. Priority 50. |
| Automation: "Follow-Up Reminder" | Trigger: TimerElapsed. Condition: conversation idle > 24h, status = open. Action: set priority = medium. Priority 100. |

#### Template: `blended`

| Resource | Configuration |
|----------|--------------|
| Queue: "Support" | SLA: answer 20s, first response 30min, resolution 4h. Hours: Mon-Fri 9-18. WrapUp: 30s. |
| Queue: "Sales" | SLA: answer 15s, first response 10min, resolution 1h. Hours: Mon-Fri 9-18. WrapUp: 30s. |
| Queue: "VIP" | SLA: answer 10s, first response 5min, resolution 30min. Hours: 24/7. WrapUp: 60s. |
| Flow: "Welcome Routing" | Unpublished. Nodes: SendMessage("Welcome to {tenant}") → ConditionNode(channel=voice → Sales, else → Support) → EndNode |
| Automation: "Auto-Close Inactive" | Same as support template |
| Automation: "SLA Breach Escalation" | Same as support template, applies to all queues |
| Automation: "VIP Detection" | Trigger: ConversationCreated. Condition: contact tag = "vip". Action: route to VIP queue. Priority 10. |

### 1.4 API Changes for Template Selection

**`POST /api/v1/management/tenants`** — Add optional field:

```
CreateMgmtTenantRequest(
    ...existing fields...,
    string? Template = null   // "support" | "sales" | "blended" | null
)
```

Validation: if `Template` is not null and not one of the 3 built-in values, return 400 Bad Request.

**`POST /api/v1/partner/customers`** — Same addition:

```
CreatePartnerCustomerRequest(
    ...existing fields...,
    string? Template = null
)
```

### 1.5 Onboarding Wizard (Frontend)

#### Trigger

When an admin logs into a tenant where `Tenant.Metadata["OnboardingCompleted"]` is absent or not `"true"`, the frontend shows the wizard overlay instead of the dashboard.

#### Wizard Steps (5)

**Step 1: Welcome**
- Display: org name, timezone, locale (pre-populated from tenant)
- Input: use-case selection (Support / Sales / Blended / Skip)
- If template was already applied at tenant creation (via API `template` param), this step shows confirmation instead of selection
- If user selects a use-case here AND no template was applied at creation, the frontend calls `POST /api/v1/admin/onboarding/apply-template` to provision template resources
- Action: saves use-case selection

**Step 2: Channels**
- Display: grid of available channels (Voice, WebChat, WhatsApp, Email, SMS, Messenger, Instagram, Telegram, Twitter, RCS, Video)
- Input: toggle on/off per channel
- This does NOT configure credentials — just records intent in tenant metadata
- Action: saves `Metadata["OnboardingChannels"]` = comma-separated list

**Step 3: First Queue**
- If template was applied: show the pre-created queue(s), allow rename and SLA edit
- If no template: create a queue with name, SLA target, business hours
- Pre-filled: Mon-Fri 9-18 in tenant timezone, SLA 80/20
- Action: creates or updates queue via existing `POST /api/v1/admin/queues`

**Step 4: Invite Team (Optional)**
- Input: 1-5 rows of (email, displayName, role dropdown: Agent/Supervisor)
- Action: creates users via existing `POST /api/v1/admin/users` and sends welcome email
- Skip button available

**Step 5: Review & Launch**
- Summary of all selections
- "Complete Setup" button
- Action: calls `POST /api/v1/admin/onboarding/complete`

#### Post-Wizard: Getting Started Checklist

Persistent sidebar widget visible after wizard completion. Items calculated dynamically:

| Item | Condition for completion |
|------|------------------------|
| Organization profile configured | Always true after wizard |
| Channels selected | Always true after wizard |
| First queue created | Always true after wizard |
| Configure channel credentials | Any channel config has non-empty credentials |
| Add your first agent | At least 1 agent record exists |
| Make a test call/chat | Metadata["OnboardingTestCompleted"] = "true" (set manually or auto-detected) |
| Customize branding | TenantBranding record exists with non-null DisplayName |

Checklist is dismissible via "Dismiss" link. Sets `Metadata["OnboardingDismissedChecklist"]` = "true".

### 1.6 Onboarding Endpoints (Backend)

4 new endpoints under `/api/v1/admin/onboarding`, authorization: `AdminOnly`:

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/admin/onboarding/status` | Returns wizard completion state + checklist items with completion status |
| POST | `/admin/onboarding/apply-template` | Apply a template to existing tenant (idempotent — skips if template already applied). Body: `{ "template": "support" }` |
| POST | `/admin/onboarding/complete` | Mark wizard as completed. Sets `Metadata["OnboardingCompleted"]` = "true" |
| PUT | `/admin/onboarding/dismiss-checklist` | Hides checklist. Sets `Metadata["OnboardingDismissedChecklist"]` = "true" |

**OnboardingStatusDto:**

```
{
  "wizardCompleted": false,
  "templateApplied": "support",
  "checklist": [
    { "key": "org_profile", "label": "Organization profile configured", "completed": true },
    { "key": "channels_selected", "label": "Channels selected", "completed": true },
    { "key": "first_queue", "label": "First queue created", "completed": false },
    { "key": "channel_credentials", "label": "Configure channel credentials", "completed": false },
    { "key": "first_agent", "label": "Add your first agent", "completed": false },
    { "key": "test_interaction", "label": "Make a test call/chat", "completed": false },
    { "key": "branding", "label": "Customize branding", "completed": false }
  ],
  "checklistDismissed": false
}
```

---

## Part 2: Impersonation Read-Only

### 2.1 Request Change

**`POST /api/v1/management/impersonate`** — Add optional field:

```
ImpersonateRequest(string TargetTenantId, bool ReadOnly = false)
```

**`ImpersonateResponse`** — Add field:

```
ImpersonateResponse(string AccessToken, DateTimeOffset ExpiresAt, 
    string TargetTenantId, string TargetTenantName, bool ReadOnly)
```

### 2.2 Permission Intersection (Token Generation)

When `readOnly=true`, `JwtTokenService.GenerateImpersonationToken` receives only view permissions:

**View permission set (23 permissions):**

```
contacts:contact:view
contacts:conversation:monitor
queues:queue:view
users:user:view
campaigns:campaign:view
reporting:realtime:view
reporting:historical:view
reporting:historical:export
quality:evaluation:view
recording:recording:play
recording:recording:export
routing:skill:view
routing:flow:view
analytics:cdr:view
analytics:cdr:export
analytics:interval:view
system:audit:view
agentassist:session:view
callanalytics:analysis:view
partner:customer:view
partner:billing:view
partner:settings:view
```

**Filter logic in ManagementImpersonationEndpoints:**

```csharp
// Existing: remove platform:* permissions
var nonPlatformPerms = callerPermissions
    .Where(p => !p.StartsWith("platform:", StringComparison.Ordinal));

if (request.ReadOnly)
{
    // Intersection with view-only permissions
    targetPermissions = nonPlatformPerms
        .Where(p => ReadOnlyPermissions.Contains(p))
        .ToHashSet();
}
else
{
    targetPermissions = nonPlatformPerms.ToHashSet();
}
```

**JWT claims added when readOnly=true:**

| Claim | Value |
|-------|-------|
| `readonly` | `"true"` |
| `impersonation_mode` | `"read_only"` |

When readOnly=false (default), these claims are absent (or `impersonation_mode` = `"full"`).

### 2.3 Middleware Safety Net

In `TenantResolutionMiddleware`, new check when `readonly=true` in JWT:

```
IsReadOnlyImpersonation(context):
  return impersonation == "true" && readonly == "true"

IsBlockedInReadOnlyMode(context):
  method = context.Request.Method
  path = context.Request.Path

  // Always allow GET and OPTIONS
  if method in ("GET", "HEAD", "OPTIONS"): return false

  // Always allow ending impersonation
  if method == "DELETE" and path matches "/api/v1/management/impersonate": return false

  // Block all other DELETE, PUT, PATCH
  if method in ("DELETE", "PUT", "PATCH"): return true

  // For POST: allow safe read-only operations
  if method == "POST":
    // SSE subscriptions
    if path contains "/sse": return false
    // Search endpoints
    if path contains "/search" or path contains "/export": return false
    // Block all other POSTs
    return true

  return false
```

Response: `403 { "error": "Operation not allowed in read-only impersonation mode" }`

### 2.4 Audit Enhancement

**AuditEntry model change** (`Platform.Audit`):

```csharp
public sealed class AuditEntry
{
    // ...existing fields...
    public string? ImpersonatorId { get; init; }  // NEW: null when not impersonating
}
```

**Postgres migration 011:**

```sql
ALTER TABLE audit_entries ADD COLUMN impersonator_id TEXT NULL;
```

**Audit write integration:** Wherever audit entries are created (endpoints, services), extract `impersonator_id` from the current user's JWT claims. If present, populate `AuditEntry.ImpersonatorId`.

The simplest approach: create a helper method `AuditContextFromHttpContext(HttpContext)` that extracts tenantId, userId, ipAddress, AND impersonatorId from claims in one place.

**ImpersonationStarted event enhancement:** Add `Mode` field to the audit metadata:

```json
{
  "targetTenantId": "acme",
  "targetTenantName": "Acme Corp",
  "mode": "read_only"
}
```

### 2.5 Frontend Changes

**useImpersonate hook:**

```typescript
useImpersonate({ targetTenantId: "acme", readOnly: true })
```

**ImpersonationState** in auth-store:

```typescript
export interface ImpersonationState {
  active: boolean;
  targetTenantId: string;
  targetTenantName: string;
  readOnly: boolean;           // NEW
  originalToken: string;
  originalTenantId: string;
  expiresAt: number;
}
```

**ImpersonationBanner** visual differentiation:

| Mode | Background | Text |
|------|-----------|------|
| Full access | `bg-amber-500` (current) | "Operating as **{tenant}**" |
| Read-only | `bg-slate-500` | "Viewing as **{tenant}** (Read-Only)" |

Both modes show countdown timer and End Impersonation button.

**UI behavior in read-only:** The frontend reads the `readonly` claim from the decoded JWT. When `readonly=true`, action buttons (create, edit, delete) can be visually disabled as a UX hint. The real enforcement is backend (permissions + middleware).

---

## Part 3: Storage & Migration

### New Migration: 011_OnboardingAudit.sql

```sql
-- Audit impersonator tracking
ALTER TABLE audit_entries ADD COLUMN impersonator_id TEXT NULL;
```

No new tables. Onboarding state stored in `Tenant.Metadata` (existing pattern).

### Metadata Keys Used

| Key | Values | Set By |
|-----|--------|--------|
| `OnboardingCompleted` | `"true"` / absent | POST /admin/onboarding/complete |
| `OnboardingTemplate` | `"support"` / `"sales"` / `"blended"` / `"none"` | TenantProvisioningService |
| `OnboardingChannels` | `"voice,webchat,email"` | Wizard step 2 |
| `OnboardingDismissedChecklist` | `"true"` / absent | PUT /admin/onboarding/dismiss-checklist |
| `OnboardingTestCompleted` | `"true"` / absent | Manual or auto-detected |

---

## Part 4: Testing

### Backend Tests (~28)

| Test File | Count | What |
|-----------|-------|------|
| TenantProvisioningServiceTests | 6 | Golden defaults applied, 3 template variations, no-template = defaults only, role cloning, CSAT survey |
| OnboardingEndpointsTests | 4 | GET status, POST complete, PUT dismiss, POST apply-template |
| ImpersonationReadOnlyTests | 6 | ReadOnly permission filter, full-mode unchanged, readonly claim in JWT, response includes ReadOnly, middleware blocks in readonly, middleware allows in full |
| ReadOnlyMiddlewareTests | 4 | Blocks PUT/DELETE/PATCH, allows GET, allows safe POST (SSE/search), blocks unsafe POST |
| AuditImpersonatorTests | 3 | ImpersonatorId populated during impersonation, null when not, mode in start event |
| ProvisioningIntegrationTests | 3 | Template via management/tenants, template via partner/customers, invalid template = 400 |
| ChecklistCalculationTests | 2 | Dynamic checklist items computed correctly, dismissed state respected |

### Frontend Tests (Platform.Web)

Not counted in backend total. The wizard pages and checklist component will need their own tests in Platform.Web (estimated ~10-15 component tests + E2E).

---

## Part 5: Scope Summary

### Files

| Component | New | Modified |
|-----------|-----|----------|
| Platform.Core (AuditEntry) | 0 | 1 |
| Platform.Api (services) | 2 (TenantProvisioningService, OnboardingEndpoints) | 0 |
| Platform.Api (endpoints) | 0 | 3 (ManagementImpersonationEndpoints, ManagementTenantEndpoints, PartnerCustomerEndpoints) |
| Platform.Api (middleware) | 0 | 1 (TenantResolutionMiddleware) |
| Platform.Api (services) | 0 | 1 (JwtTokenService) |
| Platform.Api (serialization) | 0 | 1 (ApiJsonContext) |
| Platform.Api (DI + routes) | 0 | 1 (Program.cs) |
| Platform.Storage.Postgres | 1 (migration 011) | 1 (PostgresAuditStore) |
| Platform.Storage.InMemory | 0 | 1 (ServiceCollectionExtensions) |
| Tests | 6-7 new files | 0 |
| Platform.Web | ~8 new files (wizard pages, checklist, hooks) | ~4 modified (auth store, banner, routing, sidebar) |

### Endpoint Count

Current: 55 endpoint groups → 56 (1 new: OnboardingEndpoints with 4 endpoints)

### Deferred to v1.5.0

- Custom Partner Templates CRUD (`/partner/templates`)
- Notification to impersonated user (email when someone impersonates them)
- Concurrent impersonation prevention (one admin, one target at a time)
- Industry vertical templates (Healthcare, Finance, Retail — beyond use-case templates)
- Wizard interactive tooltips / product tour overlay
- "Make a test call" integration in wizard (requires Asterisk connectivity)

### Deferred to v2.0

- Template marketplace (partners publish/share templates)
- Infrastructure-as-Code export (Terraform-style tenant config export)
- Custom wizard steps per partner (white-label wizard customization)
