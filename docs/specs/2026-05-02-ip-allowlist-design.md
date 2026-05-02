# IP Allowlist — Design Spec

**Goal:** Per-tenant IP allowlist that gates authenticated requests against a configurable set of CIDR ranges, providing enterprise-grade network-level access control for Platform.Api.

**Track:** First feature in the v1.3.0 Web roadmap follow-on (alongside SAML SSO and Compliance Reporting, both deferred to subsequent specs).

**Repos affected:** Asterisk.Platform only (Identity, Storage.Postgres, Storage.InMemory, Api, Core, Audit). No SDK / Sdk.Pro changes.

**Pinned dependencies:** SDK 1.15.1, Sdk.Pro 1.16.0-pro (no bump needed for this work).

---

## §1. Architecture

Per-tenant CIDR allowlist enforced at every authenticated request via middleware, gated by `PlanFeature.IpAllowlist`, audited via existing `Audit` infrastructure, with a `PlatformAdmin` rescue bypass.

The feature has **three orthogonal pieces** that compose:

1. **Storage** — a new `tenant_ip_allowlist` table holding CIDR entries, plus an `ip_allowlist_enabled` flag added to the existing `tenant_auth_config` table.
2. **Evaluator** — a stateless `IIpAllowlistEvaluator` that answers "is IP X allowed for tenant Y?" given a cached set of entries.
3. **Enforcement** — a per-request middleware that runs after authentication, reads the caller's IP (respecting trusted proxies), and either passes or returns 403.

The split keeps storage swappable (Postgres vs in-memory for tests), evaluator pure (testable in isolation), and middleware focused on HTTP plumbing.

---

## §2. Data model

### §2.1. New table — `tenant_ip_allowlist`

Migration `023_TenantIpAllowlist.sql` (next available number after `022_DataProtectionKeysFixNotNull.sql`):

```sql
CREATE TABLE tenant_ip_allowlist (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    cidr CIDR NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by_user_id UUID REFERENCES users(id) ON DELETE SET NULL,
    CONSTRAINT cidr_per_tenant_unique UNIQUE (tenant_id, cidr)
);

CREATE INDEX idx_tenant_ip_allowlist_tenant ON tenant_ip_allowlist(tenant_id);
```

Postgres `CIDR` type is used directly (not `TEXT`) for two reasons: (1) native validation rejects malformed values at INSERT time, (2) `inet >>= cidr` operator gives free containment checks if we ever want server-side matching. IPv4 and IPv6 are both supported by the type.

`description` is optional free text ("HQ office", "VPN gateway", "AWS NAT") — purely for operator UI.

`created_by_user_id` uses `ON DELETE SET NULL` so deleting a user does not cascade-delete their entries (the entries should outlive their author for compliance trails).

### §2.2. Extension to `tenant_auth_config`

Same migration adds:

```sql
ALTER TABLE tenant_auth_config
    ADD COLUMN ip_allowlist_enabled BOOLEAN NOT NULL DEFAULT FALSE;
```

The existing `TenantAuthConfig` record (in `Asterisk.Platform.Identity/TenantAuthConfig.cs`) gains:

```csharp
public bool IpAllowlistEnabled { get; set; }
```

The single source of truth: `IpAllowlistEnabled` answers "is enforcement active?", and `tenant_ip_allowlist` rows answer "what's allowed?". Splitting these allows the operator to keep entries staged ("I added them but didn't flip the switch yet") and avoids overloading a `count(entries) > 0` heuristic.

---

## §3. Components

### §3.1. Domain (Identity package)

**`IpAllowlistEntry.cs`** — domain record:

```csharp
public sealed class IpAllowlistEntry
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Cidr { get; init; }       // canonical "192.0.2.0/24" or "2001:db8::/32"
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? CreatedByUserId { get; init; }
}
```

`Cidr` is stored in canonical form: Postgres `CIDR` type auto-normalizes on INSERT (e.g., `192.0.2.5/24` is rejected — host bits set with prefix < 32 are an error; `192.0.2.0/24` is accepted as-is). The store rethrows the Postgres validation error as a 400 with `ip_allowlist_invalid_cidr`. The `AddAsync` validation pre-parses with `IPNetwork.Parse` to produce a friendlier error before round-tripping to Postgres.

**`ITenantIpAllowlistStore.cs`** — storage contract (mirrors `ITenantAuthConfigStore` shape):

```csharp
public interface ITenantIpAllowlistStore
{
    Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct);
    Task<IpAllowlistEntry> AddAsync(string tenantId, string cidr, string? description, Guid? createdByUserId, CancellationToken ct);
    Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct);
    Task<int> CountAsync(string tenantId, CancellationToken ct);
}
```

`CountAsync` is a separate method (not `ListAsync().Count`) so the validator can run a cheap count-only query for the "cannot enable with empty list" check without paying for materialization.

**`IIpAllowlistEvaluator.cs`** + **`DefaultIpAllowlistEvaluator.cs`** — pure evaluation:

```csharp
public interface IIpAllowlistEvaluator
{
    bool IsAllowed(IPAddress clientIp, IReadOnlyList<IpAllowlistEntry> entries);
}
```

Implementation iterates entries, parses `Cidr` to `IPNetwork` (System.Net), calls `IPNetwork.Contains(clientIp)`. O(N) sequential scan. Empty `entries` returns `false` (fail-closed — see §4.2). Caller is responsible for passing `entries` already filtered to the right tenant.

The evaluator is stateless; no caching or storage access. Caching lives at the storage layer.

### §3.2. Storage implementations

**`PostgresTenantIpAllowlistStore.cs`** in `Storage.Postgres/Stores/`:
- Uses the shared `NpgsqlDataSource` (per ADR-0015 / ADR-0008 of this repo).
- Reads `cidr::text` to return canonical form.
- Maps Postgres unique-violation `23505` to a domain conflict result (returns the existing entry, no exception).

**`InMemoryTenantIpAllowlistStore.cs`** in `Storage.InMemory/`:
- Backed by `ConcurrentDictionary<string /*tenantId*/, ConcurrentDictionary<Guid, IpAllowlistEntry>>`.
- For dev / tests / non-Postgres environments.

Both register via existing `ServiceCollectionExtensions` patterns in their respective packages.

### §3.3. Caching layer

**`CachedTenantIpAllowlistStore.cs`** in `Api/Services/` (decorator pattern, mirroring `CachedTenantAuthConfigStore`):
- Wraps the underlying store, decorates `ListAsync` only (mutations write through and invalidate).
- Backed by `IMemoryCache`, key `ip-allowlist:{tenantId}`, TTL 60s.
- Mutations (`AddAsync`, `RemoveAsync`) call `cache.Remove(key)` after the underlying call succeeds.
- The CRUD endpoints call mutations through this decorator to ensure cache coherence with their own writes.

### §3.4. Middleware

**`IpAllowlistMiddleware.cs`** in `Api/Middleware/`:
- Registered after `UseAuthentication` and before endpoints.
- Skips for any endpoint marked `[AllowAnonymous]` — checked via `httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymousMetadata>() is not null`. This covers login, refresh, health checks, OIDC callback, and any future anonymous endpoint without a hardcoded path list.
- Reads tenant ID from claims (`tenant_id` claim).
- Reads role from claims; if `PlatformAdmin`, audits `auth.ip_allowlist.bypass` and passes.
- Resolves `TenantAuthConfig` via cached store; if `IpAllowlistEnabled == false` → pass.
- Resolves entries via cached `ITenantIpAllowlistStore.ListAsync`.
- Calls `IIpAllowlistEvaluator.IsAllowed(httpContext.Connection.RemoteIpAddress, entries)`.
  - Pass → continue pipeline.
  - Fail → audit `auth.ip_allowlist.denied` with the offending IP + tenant + user, return `403 Forbidden` with `{"code": "ip_allowlist_violation"}` body.

### §3.5. CRUD endpoints

**`ManagementTenantIpAllowlistEndpoints.cs`** in `Api/Endpoints/`:

```
GET    /api/v1/management/tenants/{tenantId}/ip-allowlist
POST   /api/v1/management/tenants/{tenantId}/ip-allowlist
DELETE /api/v1/management/tenants/{tenantId}/ip-allowlist/{entryId}
```

Plus an extension to the existing `ManagementTenantSettingsEndpoints` to surface the `IpAllowlistEnabled` toggle alongside the other auth-config fields.

All endpoints require `system:tenant:configure` permission and `PlanFeature.IpAllowlist`.

DTOs (in `Api/Endpoints/Shared/` if not endpoint-local):

```csharp
public sealed record IpAllowlistEntryDto(Guid Id, string Cidr, string? Description, DateTimeOffset CreatedAt);
public sealed record IpAllowlistListResponse(bool Enabled, IReadOnlyList<IpAllowlistEntryDto> Entries);
public sealed record AddIpAllowlistEntryRequest(string Cidr, string? Description);
```

### §3.6. Plan gate

**`PlanFeature.cs`** in `Asterisk.Platform.Core/`:

```csharp
public enum PlanFeature
{
    Dialer,
    BotBasic,
    // ... existing values
    RecordingTranscription,
    IpAllowlist,    // NEW
}
```

The CRUD endpoints use the same mechanism `OidcSso` uses today (`group.RequirePlanFeature(PlanFeature.IpAllowlist)`) so unlicensed tenants get 403/404 from the management API.

The middleware uses an asymmetric semantic: if `PlanFeature.IpAllowlist` is **not** available for the tenant, the middleware silently skips enforcement (no 403). Rationale: enforcement is a runtime gate that should never break traffic for tenants whose plan does not include the feature; if a tenant downgrades while the feature was active, traffic continues to flow. The CRUD endpoints visibility, however, is a UX gate (the admin should see "this is not in your plan" rather than a working UI that mysteriously does nothing) — so endpoints are hard-gated and middleware is soft-gated. This matches how `OidcSso` is wired today.

### §3.7. Audit events

No new audit infrastructure. The middleware and endpoints emit events through the existing `IAuditService`:

| Event | Triggered by | Severity |
|-------|-------------|----------|
| `auth.ip_allowlist.enabled` | toggling `IpAllowlistEnabled` true | Info |
| `auth.ip_allowlist.disabled` | toggling false | Warning |
| `auth.ip_allowlist.entry_added` | POST entry | Info |
| `auth.ip_allowlist.entry_removed` | DELETE entry | Info |
| `auth.ip_allowlist.denied` | middleware 403 | Warning |
| `auth.ip_allowlist.bypass` | PlatformAdmin pass | Info |

Each event includes tenant ID, actor user ID, and (for denied/bypass) the offending IP and the matched/missed CIDR set.

---

## §4. Behaviour rules

### §4.1. Empty-list rejection at enable time

Toggling `IpAllowlistEnabled = true` while `tenant_ip_allowlist` has zero rows for that tenant returns `400 BadRequest` with code `ip_allowlist_enable_requires_entries`. The toggle endpoint runs `CountAsync` before the update transaction.

### §4.2. Cannot delete the last entry while enabled

`DELETE /api/v1/management/tenants/{tenantId}/ip-allowlist/{entryId}`, when the entry is the last one and `IpAllowlistEnabled == true`, returns `400 BadRequest` with code `ip_allowlist_cannot_empty_while_enabled`. The store's `RemoveAsync` runs inside a transaction that re-counts before commit.

### §4.3. Fail-closed runtime semantics

If `IpAllowlistEnabled == true` and entries are present but none match the client IP, the request is denied. There is no "warn-only" mode — once enabled, the gate is binding.

### §4.4. PlatformAdmin bypass

Users with the global `PlatformAdmin` role (not a tenant-scoped role) bypass the check. This is the rescue valve for clients who misconfigure their allowlist. Every bypass emits an audit event so the support team's actions are reviewable.

The bypass uses the role claim, not a separate flag — `PlatformAdmin` is already an audited role in the existing RBAC.

### §4.5. Trusted proxy resolution

Client IP is read via `HttpContext.Connection.RemoteIpAddress` AFTER `app.UseForwardedHeaders()` has already mutated it based on `X-Forwarded-For`. Trusted-proxy configuration:

```json
"ForwardedHeaders": {
  "TrustedProxies": ["10.0.0.0/8", "172.16.0.0/12"],
  "TrustedNetworks": []
}
```

`Program.cs` reads these at startup and configures `ForwardedHeadersOptions.KnownNetworks`. Default empty → no header trust → `RemoteIpAddress` is the raw socket peer (existing behaviour, no regression).

### §4.6. IPv4/IPv6 dual-stack

Both supported end-to-end. Postgres `CIDR` accepts either; .NET `IPAddress.Parse` and `IPNetwork.Contains` handle both. An IPv4 client cannot match an IPv6 entry and vice versa (the `IPNetwork.Contains` method enforces address-family equality).

---

## §5. Testing

### §5.1. Unit tests

**`DefaultIpAllowlistEvaluatorTests`** in `Identity.Tests/`:

| Test | Naming convention |
|------|-------------------|
| IPv4 address inside an IPv4 /24 entry → allowed | `IsAllowed_ShouldReturnTrue_WhenIpv4InRange` |
| IPv4 address outside any entry → denied | `IsAllowed_ShouldReturnFalse_WhenIpv4OutOfRange` |
| IPv6 address inside an IPv6 /48 entry → allowed | `IsAllowed_ShouldReturnTrue_WhenIpv6InRange` |
| Empty entries list → denied (fail-closed) | `IsAllowed_ShouldReturnFalse_WhenEntriesEmpty` |
| IPv4 client against IPv6-only entries → denied | `IsAllowed_ShouldReturnFalse_WhenAddressFamilyMismatch` |
| `/32` exact match | `IsAllowed_ShouldReturnTrue_WhenExactSingleHost` |
| Malformed CIDR in entries → throws / skipped | `IsAllowed_ShouldThrow_WhenCidrMalformed` |

### §5.2. Integration tests

**`IpAllowlistMiddlewareTests`** in `Api.Tests/`:
- Enabled + matching IP → 200.
- Enabled + non-matching IP → 403 + audit emitted.
- Disabled → pass-through regardless of IP.
- `PlatformAdmin` claim + non-matching IP → 200 + bypass audit.
- Plan feature absent → middleware inert (200 even with non-matching IP).
- Anonymous request → middleware skipped.

**`ManagementTenantIpAllowlistEndpointsTests`**:
- POST happy path → 201 + Location header.
- POST with malformed CIDR → 400.
- POST without `system:tenant:configure` → 403.
- DELETE last entry while enabled → 400 with `ip_allowlist_cannot_empty_while_enabled`.
- PATCH `IpAllowlistEnabled = true` with zero entries → 400 with `ip_allowlist_enable_requires_entries`.

**`PostgresTenantIpAllowlistStoreTests`**:
- Roundtrip IPv4 entry preserves canonical form.
- Roundtrip IPv6 entry preserves canonical form.
- Duplicate `(tenant_id, cidr)` → maps to conflict result, not exception.
- `ON DELETE CASCADE` from `tenants` table removes entries.

### §5.3. End-to-end / load

The repo's NBomber `LoadTests` project gets a new opt-in scenario `IpAllowlistEnforcement` that runs ~1k RPS through an enforced tenant to measure the per-request overhead added by the middleware. Target: middleware adds < 0.5 ms p99 over baseline. Not blocking for ship.

---

## §6. Migration & rollout

- **Backward-compatible:** all existing tenants get `IpAllowlistEnabled = FALSE` via the migration default. No traffic is affected on rollout.
- **Reversible:** dropping migration `023` cleanly removes the table and column. The middleware short-circuits when the column is absent (defensive read with default `false`).
- **No data backfill needed.**
- **Plan-gated:** until a tenant's plan includes `PlanFeature.IpAllowlist`, neither the endpoints nor the middleware do anything observable.

---

## §7. Non-goals (YAGNI)

- ❌ Per-role / per-user allowlists (deferred — easy additive change later if requested).
- ❌ Time-windowed entries ("only weekdays 9-5").
- ❌ Geo-based allowlist (country code).
- ❌ Self-service deactivation flow ("send rescue email link to bypass") — operationally complex; the `PlatformAdmin` bypass is sufficient.
- ❌ Web UI design — this spec is API-only. The Asterisk.Platform.Web admin page (under `src/admin/security/ip-allowlist/`) is a separate sub-project tracked in the Web repo's roadmap and built once these endpoints ship.
- ❌ Bulk import (CSV upload of entries) — single-entry POST is enough for v1; bulk is a v1.3.x add-on if real demand surfaces.

---

## §8. Open questions

None at design time. All four design decisions (scope, enforcement point, empty-list policy, operational details) were resolved during brainstorming on 2026-05-02:
- Scope: per-tenant only.
- Enforcement: every authenticated request.
- Empty-list: explicit `IpAllowlistEnabled` flag; cannot enable empty; cannot empty while enabled.
- Operational: IPv4+IPv6, trusted proxies via `ForwardedHeaders` config, `PlanFeature.IpAllowlist` gate, `PlatformAdmin` bypass.
