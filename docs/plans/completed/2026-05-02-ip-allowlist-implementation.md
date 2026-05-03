# IP Allowlist Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a per-tenant IP allowlist gated by `PlanFeature.IpAllowlist`, enforced on every authenticated request via middleware, with `PlatformAdmin` rescue bypass and full audit coverage.

**Architecture:** Three composable pieces — a Postgres-backed `tenant_ip_allowlist` table (Migration 023) with an `ip_allowlist_enabled` flag added to `tenant_auth_config`, a stateless `IIpAllowlistEvaluator`, and a per-request `IpAllowlistMiddleware`. CRUD lives at `/api/v1/management/tenants/{tenantId}/ip-allowlist` and the toggle extends `ManagementTenantSettingsEndpoints`. Cache via `IMemoryCache` decorator (60 s TTL, mirrors `CachedTenantAuthConfigStore`).

**Tech Stack:** .NET 10 Native AOT · ASP.NET Core minimal APIs · Npgsql + Dapper · Postgres 17 (`CIDR` native type) · `IMemoryCache` · xUnit + FluentAssertions for tests · existing `IAuditService` for audit emission · existing `IFeatureGateService` for plan gating.

**Spec:** `docs/specs/2026-05-02-ip-allowlist-design.md`.

---

## File Structure Overview

### NEW source files

```
src/Asterisk.Platform.Identity/
  IpAllowlistEntry.cs                          — domain record
  ITenantIpAllowlistStore.cs                   — storage contract
  IIpAllowlistEvaluator.cs                     — pure evaluator contract
  DefaultIpAllowlistEvaluator.cs               — IPNetwork.Contains-based impl

src/Asterisk.Platform.Storage.Postgres/
  Stores/PostgresTenantIpAllowlistStore.cs     — Postgres impl
  Migrations/023_TenantIpAllowlist.sql         — DDL

src/Asterisk.Platform.Storage.InMemory/
  InMemoryTenantIpAllowlistStore.cs            — in-memory impl

src/Asterisk.Platform.Api/Services/
  CachedTenantIpAllowlistStore.cs              — IMemoryCache decorator

src/Asterisk.Platform.Api/Middleware/
  IpAllowlistMiddleware.cs                     — per-request enforcement

src/Asterisk.Platform.Api/Endpoints/
  ManagementTenantIpAllowlistEndpoints.cs      — CRUD (3 endpoints + DTOs)
```

### MODIFIED source files

```
src/Asterisk.Platform.Core/PlanFeature.cs                  — add IpAllowlist enum value
src/Asterisk.Platform.Identity/TenantAuthConfig.cs         — add IpAllowlistEnabled property
src/Asterisk.Platform.Storage.Postgres/Stores/
  PostgresTenantAuthConfigStore.cs                         — read/write new column
src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs
                                                            — register PostgresTenantIpAllowlistStore
src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs
                                                            — register InMemory store
src/Asterisk.Platform.Api/Program.cs                       — register middleware,
                                                              endpoint mapping,
                                                              cache decorator,
                                                              forwarded-headers config
src/Asterisk.Platform.Api/Endpoints/
  ManagementTenantSettingsEndpoints.cs                     — surface IpAllowlistEnabled
                                                              + validation
src/Asterisk.Platform.Api/Endpoints/Shared/
  TenantSettingsDtos.cs                                    — add field to UpdateAuthSettingsDto
                                                              and AuthSettingsDto
```

### NEW test files

```
tests/Asterisk.Platform.Identity.Tests/
  DefaultIpAllowlistEvaluatorTests.cs

tests/Asterisk.Platform.Storage.Postgres.Tests/
  PostgresTenantIpAllowlistStoreTests.cs

tests/Asterisk.Platform.Api.Tests/
  IpAllowlistMiddlewareTests.cs
  ManagementTenantIpAllowlistEndpointsTests.cs
```

---

## FCM Batching

- **Phase F (Foundation):** Tasks 1–4 — types, migration, DTOs. No business logic. Can be reviewed as a single batch.
- **Phase C (Critical):** Tasks 5–10 — evaluator, stores, decorator, middleware, endpoints, settings extension. Each is a focused subagent task with TDD and individual review.
- **Phase M (Mechanical):** Tasks 11–13 — DI wiring, Program.cs, full-suite verification. Reviewable as a final batch.

---

# Phase F — Foundation

## Task 1: Add `PlanFeature.IpAllowlist` enum value

**Files:**
- Modify: `src/Asterisk.Platform.Core/PlanFeature.cs`

- [ ] **Step 1.1: Read the current enum**

Run: `cat src/Asterisk.Platform.Core/PlanFeature.cs`

- [ ] **Step 1.2: Append the new value**

Edit `src/Asterisk.Platform.Core/PlanFeature.cs`. Replace:

```csharp
public enum PlanFeature
{
    Dialer,
    BotBasic,
    BotAdvanced,
    AgentAssist,
    CallAnalytics,
    AnalyticsExport,
    Flows,
    Webhooks,
    OidcSso,
    ScheduledReports,
    KnowledgeBase,
    Recordings,
    RecordingTranscription,
}
```

with:

```csharp
public enum PlanFeature
{
    Dialer,
    BotBasic,
    BotAdvanced,
    AgentAssist,
    CallAnalytics,
    AnalyticsExport,
    Flows,
    Webhooks,
    OidcSso,
    ScheduledReports,
    KnowledgeBase,
    Recordings,
    RecordingTranscription,
    IpAllowlist,
}
```

- [ ] **Step 1.3: Build to confirm no breakage**

Run: `dotnet build src/Asterisk.Platform.Core/Asterisk.Platform.Core.csproj -c Release`
Expected: `Build succeeded` with 0 errors and 0 warnings (TreatWarningsAsErrors is on).

- [ ] **Step 1.4: Commit**

```bash
git add src/Asterisk.Platform.Core/PlanFeature.cs
git commit -m "feat(core): add PlanFeature.IpAllowlist enum value

First step of the IP allowlist track. Adds the plan-feature flag
that the new management endpoints will hard-gate on (403 when not
licensed) and the new middleware will soft-gate on (silent skip).

See docs/specs/2026-05-02-ip-allowlist-design.md §3.6."
```

---

## Task 2: Migration 023 — `tenant_ip_allowlist` + `tenant_auth_config.ip_allowlist_enabled`

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/023_TenantIpAllowlist.sql`

- [ ] **Step 2.1: Create the migration file**

Write `src/Asterisk.Platform.Storage.Postgres/Migrations/023_TenantIpAllowlist.sql`:

```sql
-- 023_TenantIpAllowlist.sql
--
-- v1.3.0 IP Allowlist (per-tenant). Adds:
--   1) `tenant_ip_allowlist` table holding CIDR entries (Postgres native CIDR type
--      → IPv4 + IPv6 both supported, server-side validation rejects malformed
--      values at INSERT time).
--   2) `tenant_auth_config.ip_allowlist_enabled` flag — separate from the entries
--      so the operator can stage entries before flipping enforcement on. Default
--      FALSE keeps existing tenants unaffected.
--
-- See docs/specs/2026-05-02-ip-allowlist-design.md §2.

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

ALTER TABLE tenant_auth_config
    ADD COLUMN ip_allowlist_enabled BOOLEAN NOT NULL DEFAULT FALSE;
```

- [ ] **Step 2.2: Verify the file is picked up by the migration runner**

Run: `ls src/Asterisk.Platform.Storage.Postgres/Migrations/ | sort -t'_' -k1 -n | tail -5`
Expected: `023_TenantIpAllowlist.sql` appears as the last entry after `022_DataProtectionKeysFixNotNull.sql`.

- [ ] **Step 2.3: Confirm `tenants(tenant_id)` and `users(id)` exist as referenceable**

Run: `grep -E "CREATE TABLE tenants|CREATE TABLE users" src/Asterisk.Platform.Storage.Postgres/Migrations/001_InitialSchema.sql`
Expected: both tables found. (`tenants.tenant_id` is `TEXT`, `users.id` is `UUID`.) If not, the FK paths above must be adjusted.

- [ ] **Step 2.4: Build the Postgres project**

Run: `dotnet build src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj -c Release`
Expected: Build succeeds. The .sql file is an embedded resource (verify via `<EmbeddedResource Include="Migrations/*.sql" />` already present in the .csproj).

- [ ] **Step 2.5: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Migrations/023_TenantIpAllowlist.sql
git commit -m "feat(storage): migration 023 — tenant_ip_allowlist + ip_allowlist_enabled

Adds the per-tenant CIDR table (native CIDR type, IPv4+IPv6) and a
boolean flag on tenant_auth_config that gates enforcement. Default
FALSE preserves existing tenant behaviour.

cidr_per_tenant_unique prevents duplicate ranges. ON DELETE CASCADE
on tenant_id removes entries when a tenant is deleted; ON DELETE
SET NULL on created_by_user_id keeps entries when their author is
removed (compliance trail).

See docs/specs/2026-05-02-ip-allowlist-design.md §2."
```

---

## Task 3: Extend `TenantAuthConfig` record with `IpAllowlistEnabled`

**Files:**
- Modify: `src/Asterisk.Platform.Identity/TenantAuthConfig.cs`
- Modify: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs`

- [ ] **Step 3.1: Add property to record**

Edit `src/Asterisk.Platform.Identity/TenantAuthConfig.cs`. After the existing `ImpersonationAutoTimeoutMinutes` property and BEFORE `UpdatedAt`:

```csharp
    /// <summary>
    /// v1.3.0 IP Allowlist — when true, requests from IPs not matching any
    /// row in tenant_ip_allowlist are rejected with 403. When false, the
    /// allowlist is dormant regardless of the entries that may exist.
    /// Cannot be flipped to true while the entry list is empty.
    /// See docs/specs/2026-05-02-ip-allowlist-design.md §4.
    /// </summary>
    public bool IpAllowlistEnabled { get; set; }
```

- [ ] **Step 3.2: Update Postgres SELECT**

In `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs`, the `GetAsync` SELECT list. Replace:

```csharp
            "SELECT tenant_id, mfa_policy, mfa_required_roles, password_min_length, password_require_uppercase, " +
            "password_require_number, password_require_special, lockout_threshold, lockout_duration_minutes, " +
            "session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, oidc_authority, " +
            "oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, updated_at " +
            "FROM tenant_auth_config WHERE tenant_id = @TenantId",
```

with (note: appending `, ip_allowlist_enabled` before `, updated_at`):

```csharp
            "SELECT tenant_id, mfa_policy, mfa_required_roles, password_min_length, password_require_uppercase, " +
            "password_require_number, password_require_special, lockout_threshold, lockout_duration_minutes, " +
            "session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, oidc_authority, " +
            "oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, ip_allowlist_enabled, updated_at " +
            "FROM tenant_auth_config WHERE tenant_id = @TenantId",
```

- [ ] **Step 3.3: Update Postgres INSERT/UPDATE**

In the same file, in `SaveAsync`, the column list and VALUES list. Replace:

```csharp
            "INSERT INTO tenant_auth_config (tenant_id, mfa_policy, mfa_required_roles, password_min_length, " +
            "password_require_uppercase, password_require_number, password_require_special, lockout_threshold, " +
            "lockout_duration_minutes, session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, " +
            "oidc_authority, oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, updated_at) " +
            "VALUES (@TenantId, @MfaPolicy, @MfaRequiredRoles, @PasswordMinLength, @PasswordRequireUppercase, " +
            "@PasswordRequireNumber, @PasswordRequireSpecial, @LockoutThreshold, @LockoutDurationMinutes, " +
            "@SessionIdleTimeoutMinutes, @SessionAbsoluteTimeoutHours, @OidcEnabled, @OidcAuthority, " +
            "@OidcClientId, @OidcClientSecret, @OidcAutoCreateUsers, @OidcDefaultRole, " +
            "@ImpersonationMaxConcurrentSessions, @ImpersonationAutoTimeoutMinutes, @UpdatedAt) " +
```

with:

```csharp
            "INSERT INTO tenant_auth_config (tenant_id, mfa_policy, mfa_required_roles, password_min_length, " +
            "password_require_uppercase, password_require_number, password_require_special, lockout_threshold, " +
            "lockout_duration_minutes, session_idle_timeout_minutes, session_absolute_timeout_hours, oidc_enabled, " +
            "oidc_authority, oidc_client_id, oidc_client_secret, oidc_auto_create_users, oidc_default_role, " +
            "impersonation_max_concurrent_sessions, impersonation_auto_timeout_minutes, ip_allowlist_enabled, updated_at) " +
            "VALUES (@TenantId, @MfaPolicy, @MfaRequiredRoles, @PasswordMinLength, @PasswordRequireUppercase, " +
            "@PasswordRequireNumber, @PasswordRequireSpecial, @LockoutThreshold, @LockoutDurationMinutes, " +
            "@SessionIdleTimeoutMinutes, @SessionAbsoluteTimeoutHours, @OidcEnabled, @OidcAuthority, " +
            "@OidcClientId, @OidcClientSecret, @OidcAutoCreateUsers, @OidcDefaultRole, " +
            "@ImpersonationMaxConcurrentSessions, @ImpersonationAutoTimeoutMinutes, @IpAllowlistEnabled, @UpdatedAt) " +
```

Then in the `ON CONFLICT DO UPDATE SET` clause, replace:

```csharp
            "  impersonation_max_concurrent_sessions = EXCLUDED.impersonation_max_concurrent_sessions, " +
            "  impersonation_auto_timeout_minutes = EXCLUDED.impersonation_auto_timeout_minutes, " +
            "  updated_at = EXCLUDED.updated_at",
```

with:

```csharp
            "  impersonation_max_concurrent_sessions = EXCLUDED.impersonation_max_concurrent_sessions, " +
            "  impersonation_auto_timeout_minutes = EXCLUDED.impersonation_auto_timeout_minutes, " +
            "  ip_allowlist_enabled = EXCLUDED.ip_allowlist_enabled, " +
            "  updated_at = EXCLUDED.updated_at",
```

Then in the parameter object passed to `ExecuteAsync`, add `config.IpAllowlistEnabled` between `config.ImpersonationAutoTimeoutMinutes` and `config.UpdatedAt`:

```csharp
                config.ImpersonationMaxConcurrentSessions,
                config.ImpersonationAutoTimeoutMinutes,
                config.IpAllowlistEnabled,
                config.UpdatedAt,
```

- [ ] **Step 3.4: Update the Dapper row class**

In the same file, the `TenantAuthConfigRow` private class (after `impersonation_auto_timeout_minutes`):

```csharp
        public bool ip_allowlist_enabled { get; init; }
```

And in `TenantAuthConfigRow.ToTenantAuthConfig()` (locate the method body — it constructs the `TenantAuthConfig` object), add the assignment:

```csharp
            IpAllowlistEnabled = ip_allowlist_enabled,
```

(matches the existing pattern of the other property assignments — find them via `grep -n "ImpersonationAutoTimeoutMinutes" PostgresTenantAuthConfigStore.cs`).

- [ ] **Step 3.5: Build**

Run: `dotnet build src/Asterisk.Platform.Identity/Asterisk.Platform.Identity.csproj src/Asterisk.Platform.Storage.Postgres/Asterisk.Platform.Storage.Postgres.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 3.6: Commit**

```bash
git add src/Asterisk.Platform.Identity/TenantAuthConfig.cs src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantAuthConfigStore.cs
git commit -m "feat(identity): TenantAuthConfig.IpAllowlistEnabled flag

Wires the new ip_allowlist_enabled column from migration 023 into
the TenantAuthConfig record + Postgres store roundtrip. Defaults
to false on tenants without an explicit save (existing behaviour
preserved).

See docs/specs/2026-05-02-ip-allowlist-design.md §2.2."
```

---

## Task 4: Domain types — `IpAllowlistEntry` + `ITenantIpAllowlistStore`

**Files:**
- Create: `src/Asterisk.Platform.Identity/IpAllowlistEntry.cs`
- Create: `src/Asterisk.Platform.Identity/ITenantIpAllowlistStore.cs`

- [ ] **Step 4.1: Write the entry record**

Create `src/Asterisk.Platform.Identity/IpAllowlistEntry.cs`:

```csharp
namespace Asterisk.Platform.Identity;

/// <summary>
/// A single CIDR entry in a tenant's IP allowlist.
/// Cidr is stored in canonical form (Postgres CIDR type normalises on insert).
/// </summary>
public sealed class IpAllowlistEntry
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string Cidr { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>
    /// User ID of the actor who added the entry. Stored as TEXT (no FK to
    /// users) to match this repo's convention — users PK is composite
    /// (tenant_id, user_id) and other audit-bearing tables (refresh_tokens,
    /// role_assignments) likewise store user_id TEXT without FK.
    /// </summary>
    public string? CreatedByUserId { get; init; }
}
```

- [ ] **Step 4.2: Write the store contract**

Create `src/Asterisk.Platform.Identity/ITenantIpAllowlistStore.cs`:

```csharp
namespace Asterisk.Platform.Identity;

/// <summary>
/// Storage contract for per-tenant IP allowlist entries.
/// Implementations: PostgresTenantIpAllowlistStore (production),
/// InMemoryTenantIpAllowlistStore (tests + dev).
/// </summary>
public interface ITenantIpAllowlistStore
{
    Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct);

    Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct);

    Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct);

    Task<int> CountAsync(string tenantId, CancellationToken ct);
}
```

- [ ] **Step 4.3: Build the Identity package**

Run: `dotnet build src/Asterisk.Platform.Identity/Asterisk.Platform.Identity.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 4.4: Commit**

```bash
git add src/Asterisk.Platform.Identity/IpAllowlistEntry.cs src/Asterisk.Platform.Identity/ITenantIpAllowlistStore.cs
git commit -m "feat(identity): IpAllowlistEntry + ITenantIpAllowlistStore contract

Domain record + storage contract for the IP allowlist track. Deliberately
no behaviour here — pure types so the evaluator and middleware can be
written test-first against an in-memory fake.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.1."
```

---

# Phase C — Critical components

## Task 5: `IIpAllowlistEvaluator` + `DefaultIpAllowlistEvaluator` (TDD)

**Files:**
- Create: `src/Asterisk.Platform.Identity/IIpAllowlistEvaluator.cs`
- Create: `src/Asterisk.Platform.Identity/DefaultIpAllowlistEvaluator.cs`
- Create: `tests/Asterisk.Platform.Identity.Tests/DefaultIpAllowlistEvaluatorTests.cs`

- [ ] **Step 5.1: Write the failing tests**

Create `tests/Asterisk.Platform.Identity.Tests/DefaultIpAllowlistEvaluatorTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Identity.Tests;

public class DefaultIpAllowlistEvaluatorTests
{
    private readonly DefaultIpAllowlistEvaluator _sut = new();

    private static IpAllowlistEntry Entry(string cidr) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "t1",
        Cidr = cidr,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenIpv4InRange()
    {
        var entries = new[] { Entry("192.0.2.0/24") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenIpv4OutOfRange()
    {
        var entries = new[] { Entry("192.0.2.0/24") };
        _sut.IsAllowed(IPAddress.Parse("203.0.113.5"), entries).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenIpv6InRange()
    {
        var entries = new[] { Entry("2001:db8::/32") };
        _sut.IsAllowed(IPAddress.Parse("2001:db8:1234::5"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenEntriesEmpty()
    {
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), Array.Empty<IpAllowlistEntry>()).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnFalse_WhenAddressFamilyMismatch()
    {
        var entries = new[] { Entry("2001:db8::/32") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_ShouldReturnTrue_WhenExactSingleHost()
    {
        var entries = new[] { Entry("192.0.2.45/32") };
        _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries).Should().BeTrue();
    }

    [Fact]
    public void IsAllowed_ShouldThrow_WhenCidrMalformed()
    {
        var entries = new[] { Entry("not-a-cidr") };
        Action act = () => _sut.IsAllowed(IPAddress.Parse("192.0.2.45"), entries);
        act.Should().Throw<FormatException>();
    }
}
```

- [ ] **Step 5.2: Run the tests — confirm they fail**

Run: `dotnet test tests/Asterisk.Platform.Identity.Tests/Asterisk.Platform.Identity.Tests.csproj --filter "FullyQualifiedName~DefaultIpAllowlistEvaluatorTests"`
Expected: All 7 tests fail with `CS0246` ("type or namespace 'DefaultIpAllowlistEvaluator' could not be found") because the production type does not exist yet.

- [ ] **Step 5.3: Write the interface**

Create `src/Asterisk.Platform.Identity/IIpAllowlistEvaluator.cs`:

```csharp
using System.Net;

namespace Asterisk.Platform.Identity;

/// <summary>
/// Pure evaluator: given a client IP and a tenant's allowlist entries,
/// decide whether the IP is allowed. Stateless; no caching, no storage.
/// Empty entries returns false (fail-closed — see spec §4.3).
/// </summary>
public interface IIpAllowlistEvaluator
{
    bool IsAllowed(IPAddress clientIp, IReadOnlyList<IpAllowlistEntry> entries);
}
```

- [ ] **Step 5.4: Write the implementation**

Create `src/Asterisk.Platform.Identity/DefaultIpAllowlistEvaluator.cs`:

```csharp
using System.Net;

namespace Asterisk.Platform.Identity;

/// <summary>
/// Sequential CIDR-match evaluator. O(N) over entries; N is typically small
/// (≤ 50 entries per tenant in practice) so a trie is overkill.
/// Each Cidr string is parsed via <see cref="IPNetwork.Parse(string)"/>;
/// malformed values bubble FormatException up to the caller (the store
/// guarantees canonical form on insert, so a malformed value implies
/// corruption that must surface).
/// </summary>
public sealed class DefaultIpAllowlistEvaluator : IIpAllowlistEvaluator
{
    public bool IsAllowed(IPAddress clientIp, IReadOnlyList<IpAllowlistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return false;

        foreach (var entry in entries)
        {
            var network = IPNetwork.Parse(entry.Cidr);
            if (network.BaseAddress.AddressFamily != clientIp.AddressFamily)
                continue;
            if (network.Contains(clientIp))
                return true;
        }

        return false;
    }
}
```

- [ ] **Step 5.5: Run tests — confirm they pass**

Run: `dotnet test tests/Asterisk.Platform.Identity.Tests/Asterisk.Platform.Identity.Tests.csproj --filter "FullyQualifiedName~DefaultIpAllowlistEvaluatorTests"`
Expected: 7 passed, 0 failed.

- [ ] **Step 5.6: Commit**

```bash
git add src/Asterisk.Platform.Identity/IIpAllowlistEvaluator.cs src/Asterisk.Platform.Identity/DefaultIpAllowlistEvaluator.cs tests/Asterisk.Platform.Identity.Tests/DefaultIpAllowlistEvaluatorTests.cs
git commit -m "feat(identity): IIpAllowlistEvaluator + DefaultIpAllowlistEvaluator

Pure CIDR-match evaluator (O(N) sequential, IPv4 + IPv6, family-aware).
Empty entries returns false (fail-closed per spec §4.3). Malformed
Cidr surfaces FormatException — store guarantees canonical form so
this should never trigger in production.

7 unit tests: IPv4 in/out range, IPv6 in range, /32 exact match,
empty list, address family mismatch, malformed cidr.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.1, §5.1."
```

---

## Task 6: `InMemoryTenantIpAllowlistStore` (TDD)

**Files:**
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantIpAllowlistStore.cs`
- Reuse for tests: declared by Task 7 (Postgres tests share fixture style; the InMemory store has its own dedicated round-trip test in Task 6)

- [ ] **Step 6.1: Add the InMemory implementation**

Create `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantIpAllowlistStore.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

/// <summary>
/// In-memory implementation of <see cref="ITenantIpAllowlistStore"/> for
/// dev / tests / non-Postgres environments. Thread-safe via ConcurrentDictionary.
/// Validates the cidr format eagerly via <see cref="IPNetwork.Parse(string)"/>
/// so test code surfaces malformed values up front.
/// </summary>
public sealed class InMemoryTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, IpAllowlistEntry>> _byTenant = new();

    public Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult<IReadOnlyList<IpAllowlistEntry>>(Array.Empty<IpAllowlistEntry>());
        return Task.FromResult<IReadOnlyList<IpAllowlistEntry>>(entries.Values.ToArray());
    }

    public Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(cidr);

        // Validate cidr eagerly so test setup surfaces malformed input.
        var canonical = System.Net.IPNetwork.Parse(cidr).ToString();

        var bucket = _byTenant.GetOrAdd(tenantId, static _ => new ConcurrentDictionary<Guid, IpAllowlistEntry>());

        // Reject duplicates (cidr_per_tenant_unique mirror).
        var existing = bucket.Values.FirstOrDefault(e => e.Cidr == canonical);
        if (existing is not null)
            return Task.FromResult(existing);

        var entry = new IpAllowlistEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Cidr = canonical,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
        };
        bucket[entry.Id] = entry;
        return Task.FromResult(entry);
    }

    public Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult(false);
        return Task.FromResult(entries.TryRemove(entryId, out _));
    }

    public Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        if (!_byTenant.TryGetValue(tenantId, out var entries))
            return Task.FromResult(0);
        return Task.FromResult(entries.Count);
    }
}
```

- [ ] **Step 6.2: Build the InMemory package**

Run: `dotnet build src/Asterisk.Platform.Storage.InMemory/Asterisk.Platform.Storage.InMemory.csproj -c Release`
Expected: Build succeeds.

- [ ] **Step 6.3: Commit**

```bash
git add src/Asterisk.Platform.Storage.InMemory/InMemoryTenantIpAllowlistStore.cs
git commit -m "feat(storage): InMemoryTenantIpAllowlistStore for dev + tests

Mirrors Postgres semantics: canonical cidr form via IPNetwork.Parse,
duplicate (tenant, cidr) returns existing, count is per-tenant.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.2."
```

---

## Task 7: `PostgresTenantIpAllowlistStore` (TDD with integration test)

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantIpAllowlistStore.cs`
- Create: `tests/Asterisk.Platform.Storage.Postgres.Tests/PostgresTenantIpAllowlistStoreTests.cs`

- [ ] **Step 7.1: Write integration tests against a Postgres test container or local DB**

Confirm what fixture pattern the existing Postgres tests use:

Run: `head -50 tests/Asterisk.Platform.Storage.Postgres.Tests/PostgresUserStoreTests.cs 2>/dev/null || ls tests/Asterisk.Platform.Storage.Postgres.Tests/`

If a `[Collection("postgres")]` or similar shared fixture exists, mirror it. The standard pattern in this repo (per existing tests) is a class-level fixture that runs migrations against a local Postgres on `localhost:5432` from `appsettings.Test.json`.

Create `tests/Asterisk.Platform.Storage.Postgres.Tests/PostgresTenantIpAllowlistStoreTests.cs`:

```csharp
using Asterisk.Platform.Identity;
using Asterisk.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace Asterisk.Platform.Storage.Postgres.Tests;

[Collection("postgres")]
public class PostgresTenantIpAllowlistStoreTests
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresTenantIpAllowlistStore _sut;
    private readonly string _tenantId = $"t-{Guid.NewGuid():N}";

    public PostgresTenantIpAllowlistStoreTests(PostgresFixture fixture)
    {
        _dataSource = fixture.DataSource;
        _sut = new PostgresTenantIpAllowlistStore(_dataSource);
        // Seed the tenant — the FK from tenant_ip_allowlist requires it to exist.
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO tenants (tenant_id, name, status, created_at) VALUES (@t, @t, 'active', now()) ON CONFLICT DO NOTHING",
            conn);
        cmd.Parameters.AddWithValue("t", _tenantId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task AddAsync_ShouldRoundtrip_WhenIpv4Cidr()
    {
        var added = await _sut.AddAsync(_tenantId, "192.0.2.0/24", "office", null, default);
        added.Cidr.Should().Be("192.0.2.0/24");

        var list = await _sut.ListAsync(_tenantId, default);
        list.Should().ContainSingle(e => e.Id == added.Id && e.Description == "office");
    }

    [Fact]
    public async Task AddAsync_ShouldRoundtrip_WhenIpv6Cidr()
    {
        var added = await _sut.AddAsync(_tenantId, "2001:db8::/32", "v6 vpn", null, default);
        added.Cidr.Should().Be("2001:db8::/32");
    }

    [Fact]
    public async Task AddAsync_ShouldReturnExisting_WhenDuplicateCidr()
    {
        var first = await _sut.AddAsync(_tenantId, "203.0.113.0/24", "first", null, default);
        var second = await _sut.AddAsync(_tenantId, "203.0.113.0/24", "second", null, default);
        second.Id.Should().Be(first.Id);
        second.Description.Should().Be("first"); // existing wins
    }

    [Fact]
    public async Task RemoveAsync_ShouldReturnTrue_WhenEntryExists()
    {
        var added = await _sut.AddAsync(_tenantId, "198.51.100.0/24", null, null, default);
        var removed = await _sut.RemoveAsync(_tenantId, added.Id, default);
        removed.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_ShouldReturnFalse_WhenEntryMissing()
    {
        var removed = await _sut.RemoveAsync(_tenantId, Guid.NewGuid(), default);
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_ShouldReturnEntryCount()
    {
        await _sut.AddAsync(_tenantId, "10.0.0.0/8", null, null, default);
        await _sut.AddAsync(_tenantId, "172.16.0.0/12", null, null, default);
        var count = await _sut.CountAsync(_tenantId, default);
        count.Should().Be(2);
    }
}
```

If `PostgresFixture` does not exist by that name, locate the actual fixture: `grep -rn "Collection.*postgres\|class.*PostgresFixture\|IClassFixture" tests/Asterisk.Platform.Storage.Postgres.Tests/ | head -5`. Adjust the constructor parameter type accordingly.

- [ ] **Step 7.2: Run tests — confirm they fail**

Run: `dotnet test tests/Asterisk.Platform.Storage.Postgres.Tests/Asterisk.Platform.Storage.Postgres.Tests.csproj --filter "FullyQualifiedName~PostgresTenantIpAllowlistStoreTests"`
Expected: All 6 tests fail with `CS0246` (`PostgresTenantIpAllowlistStore` not defined).

- [ ] **Step 7.3: Write the Postgres store**

Create `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantIpAllowlistStore.cs`:

```csharp
using Dapper;
using Npgsql;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantIpAllowlistStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<IpAllowlistRow>(
            "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
            "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId ORDER BY created_at",
            new { TenantId = tenantId });
        return rows.Select(r => r.ToEntry()).ToArray();
    }

    public async Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleAsync<IpAllowlistRow>(
                "INSERT INTO tenant_ip_allowlist (tenant_id, cidr, description, created_by_user_id) " +
                "VALUES (@TenantId, @Cidr::cidr, @Description, @CreatedByUserId) " +
                "RETURNING id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id",
                new { TenantId = tenantId, Cidr = cidr, Description = description, CreatedByUserId = createdByUserId });
            return row.ToEntry();
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Unique violation on (tenant_id, cidr) — return the existing row instead of throwing.
            var existing = await conn.QuerySingleAsync<IpAllowlistRow>(
                "SELECT id, tenant_id, cidr::text AS cidr, description, created_at, created_by_user_id " +
                "FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND cidr = @Cidr::cidr",
                new { TenantId = tenantId, Cidr = cidr });
            return existing.ToEntry();
        }
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(
            "DELETE FROM tenant_ip_allowlist WHERE tenant_id = @TenantId AND id = @Id",
            new { TenantId = tenantId, Id = entryId });
        return rows > 0;
    }

    public async Task<int> CountAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tenant_ip_allowlist WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
    }

    private sealed class IpAllowlistRow
    {
        public Guid id { get; init; }
        public string tenant_id { get; init; } = null!;
        public string cidr { get; init; } = null!;
        public string? description { get; init; }
        public DateTimeOffset created_at { get; init; }
        public string? created_by_user_id { get; init; }

        public IpAllowlistEntry ToEntry() => new()
        {
            Id = id,
            TenantId = tenant_id,
            Cidr = cidr,
            Description = description,
            CreatedAt = created_at,
            CreatedByUserId = created_by_user_id,
        };
    }
}
```

- [ ] **Step 7.4: Run tests — confirm they pass**

Run: `dotnet test tests/Asterisk.Platform.Storage.Postgres.Tests/Asterisk.Platform.Storage.Postgres.Tests.csproj --filter "FullyQualifiedName~PostgresTenantIpAllowlistStoreTests"`
Expected: 6 passed, 0 failed.

- [ ] **Step 7.5: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantIpAllowlistStore.cs tests/Asterisk.Platform.Storage.Postgres.Tests/PostgresTenantIpAllowlistStoreTests.cs
git commit -m "feat(storage): PostgresTenantIpAllowlistStore + integration tests

Dapper-based store wired through the shared NpgsqlDataSource (per
ADR-0015). Casts cidr column to text when reading so callers see a
canonical string rather than NpgsqlInet. PostgresException 23505
(unique violation) maps to 'return existing entry' instead of
throwing — matches the design's idempotent-add semantics.

6 integration tests cover IPv4/IPv6 roundtrip, duplicate handling,
remove (existing + missing), and count.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.2, §5.2."
```

---

## Task 8: `CachedTenantIpAllowlistStore` decorator (TDD)

**Files:**
- Create: `src/Asterisk.Platform.Api/Services/CachedTenantIpAllowlistStore.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/CachedTenantIpAllowlistStoreTests.cs`

- [ ] **Step 8.1: Write failing tests**

Create `tests/Asterisk.Platform.Api.Tests/CachedTenantIpAllowlistStoreTests.cs`:

```csharp
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public class CachedTenantIpAllowlistStoreTests
{
    private readonly InMemoryTenantIpAllowlistStore _inner = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CachedTenantIpAllowlistStore _sut;

    public CachedTenantIpAllowlistStoreTests()
    {
        _sut = new CachedTenantIpAllowlistStore(_inner, _cache);
    }

    [Fact]
    public async Task ListAsync_ShouldHitCache_OnSecondCall()
    {
        await _inner.AddAsync("t1", "10.0.0.0/8", null, null, default);

        var first = await _sut.ListAsync("t1", default);

        // Mutate inner directly to prove the decorator returned cached data.
        await _inner.AddAsync("t1", "172.16.0.0/12", null, null, default);

        var second = await _sut.ListAsync("t1", default);
        second.Should().HaveCount(first.Count);
    }

    [Fact]
    public async Task AddAsync_ShouldInvalidateCache()
    {
        await _inner.AddAsync("t1", "10.0.0.0/8", null, null, default);
        var warmup = await _sut.ListAsync("t1", default);
        warmup.Should().HaveCount(1);

        // Add through the decorator so its mutation path runs.
        await _sut.AddAsync("t1", "172.16.0.0/12", null, null, default);

        var after = await _sut.ListAsync("t1", default);
        after.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveAsync_ShouldInvalidateCache()
    {
        var entry = await _sut.AddAsync("t1", "10.0.0.0/8", null, null, default);
        var warmup = await _sut.ListAsync("t1", default);
        warmup.Should().HaveCount(1);

        await _sut.RemoveAsync("t1", entry.Id, default);

        var after = await _sut.ListAsync("t1", default);
        after.Should().BeEmpty();
    }
}
```

- [ ] **Step 8.2: Run tests — confirm they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~CachedTenantIpAllowlistStoreTests"`
Expected: 3 tests fail (`CS0246` on `CachedTenantIpAllowlistStore`).

- [ ] **Step 8.3: Implement the decorator**

Create `src/Asterisk.Platform.Api/Services/CachedTenantIpAllowlistStore.cs`:

```csharp
using Asterisk.Platform.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// IMemoryCache decorator over <see cref="ITenantIpAllowlistStore"/>.
/// Caches ListAsync results for 60s; mutations write through and invalidate.
/// Mirrors the CachedTenantAuthConfigStore pattern (AHH Phase 1).
/// </summary>
internal sealed class CachedTenantIpAllowlistStore : ITenantIpAllowlistStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly ITenantIpAllowlistStore _inner;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl;

    public CachedTenantIpAllowlistStore(
        ITenantIpAllowlistStore inner,
        IMemoryCache cache,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
        _ttl = ttl ?? DefaultTtl;
    }

    public static string CacheKey(string tenantId) => $"ip-allowlist:{tenantId}";

    public async Task<IReadOnlyList<IpAllowlistEntry>> ListAsync(string tenantId, CancellationToken ct)
    {
        var key = CacheKey(tenantId);
        if (_cache.TryGetValue<IReadOnlyList<IpAllowlistEntry>>(key, out var cached) && cached is not null)
            return cached;

        var fresh = await _inner.ListAsync(tenantId, ct);
        _cache.Set(key, fresh, _ttl);
        return fresh;
    }

    public async Task<IpAllowlistEntry> AddAsync(
        string tenantId,
        string cidr,
        string? description,
        string? createdByUserId,
        CancellationToken ct)
    {
        var entry = await _inner.AddAsync(tenantId, cidr, description, createdByUserId, ct);
        _cache.Remove(CacheKey(tenantId));
        return entry;
    }

    public async Task<bool> RemoveAsync(string tenantId, Guid entryId, CancellationToken ct)
    {
        var removed = await _inner.RemoveAsync(tenantId, entryId, ct);
        if (removed)
            _cache.Remove(CacheKey(tenantId));
        return removed;
    }

    public Task<int> CountAsync(string tenantId, CancellationToken ct) => _inner.CountAsync(tenantId, ct);
}
```

- [ ] **Step 8.4: Run tests — confirm they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~CachedTenantIpAllowlistStoreTests"`
Expected: 3 passed, 0 failed.

- [ ] **Step 8.5: Commit**

```bash
git add src/Asterisk.Platform.Api/Services/CachedTenantIpAllowlistStore.cs tests/Asterisk.Platform.Api.Tests/CachedTenantIpAllowlistStoreTests.cs
git commit -m "feat(api): CachedTenantIpAllowlistStore decorator

IMemoryCache decorator (60s TTL) over ITenantIpAllowlistStore. Mutations
invalidate the cache key locally; cross-replica invalidation is not
needed at v1 because allowlists change rarely (operator action) and
60s staleness is acceptable.

3 unit tests cover cache hit, add invalidation, remove invalidation.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.3."
```

---

## Task 9: `IpAllowlistMiddleware` (TDD)

**Files:**
- Create: `src/Asterisk.Platform.Api/Middleware/IpAllowlistMiddleware.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/IpAllowlistMiddlewareTests.cs`

- [ ] **Step 9.1: Inspect existing middleware test pattern**

Run: `find tests/Asterisk.Platform.Api.Tests -name "*Middleware*Tests.cs" | head -3`

Pick the closest match (e.g., `LicenseGateMiddlewareTests.cs` or `TenantStatusMiddlewareTests.cs`) for fixture style. The standard pattern in this repo uses an in-process `WebApplicationFactory` or a hand-rolled `TestServer` with `IServiceCollection`.

- [ ] **Step 9.2: Write failing tests**

Create `tests/Asterisk.Platform.Api.Tests/IpAllowlistMiddlewareTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public class IpAllowlistMiddlewareTests
{
    private const string TenantId = "t1";

    private static IHost BuildHost(
        ITenantIpAllowlistStore store,
        ITenantAuthConfigStore authConfigStore,
        IFeatureGateService featureGate,
        IAuditService audit,
        Action<HttpContext>? seedClaims = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(store);
                        services.AddSingleton(authConfigStore);
                        services.AddSingleton(featureGate);
                        services.AddSingleton(audit);
                        services.AddSingleton<IIpAllowlistEvaluator, DefaultIpAllowlistEvaluator>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.Use(async (ctx, next) =>
                        {
                            seedClaims?.Invoke(ctx);
                            await next();
                        });
                        app.UseMiddleware<IpAllowlistMiddleware>();
                        app.UseEndpoints(e =>
                        {
                            e.MapGet("/protected", () => Results.Ok("ok"));
                            e.MapGet("/anon", () => Results.Ok("ok")).AllowAnonymous();
                        });
                    });
            })
            .Build();
    }

    private static void SetClient(HttpContext ctx, string ip, string tenant, string? role = null)
    {
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var claims = new List<Claim> { new("tenant_id", tenant) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task Should200_WhenAllowlistDisabled()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = false }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenIpInAllowlist()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "192.0.2.45", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should403_WhenIpNotInAllowlist()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should200_WhenPlatformAdminBypass()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId, role: "PlatformAdmin"));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenFeatureNotLicensed()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        await allowlist.AddAsync(TenantId, "192.0.2.0/24", null, null, default);
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: false), // not licensed
            new NoopAuditService(),
            ctx => SetClient(ctx, "203.0.113.99", TenantId));
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/protected");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should200_WhenAnonymousEndpoint()
    {
        var allowlist = new InMemoryTenantIpAllowlistStore();
        var authConfig = new InMemoryTenantAuthConfigStore();
        await authConfig.SaveAsync(new TenantAuthConfig { TenantId = TenantId, IpAllowlistEnabled = true }, default);

        using var host = BuildHost(
            allowlist, authConfig,
            new StubFeatureGate(featureEnabled: true),
            new NoopAuditService());
        await host.StartAsync();

        var resp = await host.GetTestClient().GetAsync("/anon");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Stubs ────────────────────────────────────────────────────────────

    private sealed class StubFeatureGate(bool featureEnabled) : IFeatureGateService
    {
        public bool IsFeatureEnabled(string tenantId, PlanFeature feature) => featureEnabled;
    }

    private sealed class NoopAuditService : IAuditService
    {
        public Task RecordAsync(
            TenantId tenantId, string category, string action, string severity,
            string actorId, string actorType, string? targetId = null, string? targetType = null,
            Guid? correlationId = null, AuditChanges? changes = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.CompletedTask;

        [Obsolete]
        public Task LogAsync(TenantId tenantId, string action, string entityType, string entityId,
            string? performedBy = null, IReadOnlyDictionary<string, string>? details = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
```

- [ ] **Step 9.3: Run tests — confirm they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~IpAllowlistMiddlewareTests"`
Expected: 6 fail (`CS0246` on `IpAllowlistMiddleware`).

- [ ] **Step 9.4: Write the middleware**

Create `src/Asterisk.Platform.Api/Middleware/IpAllowlistMiddleware.cs`:

```csharp
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace Asterisk.Platform.Api.Middleware;

/// <summary>
/// Enforces the per-tenant IP allowlist on every authenticated request.
/// Skipped for endpoints marked [AllowAnonymous] (covers login, refresh,
/// health, OIDC callback). Soft-gated: if the tenant's plan does not
/// include PlanFeature.IpAllowlist, the middleware is silently inert.
/// PlatformAdmin role bypasses the check (rescue valve, audited).
/// See docs/specs/2026-05-02-ip-allowlist-design.md §3.4 + §4.
/// </summary>
internal sealed class IpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IpAllowlistMiddleware> _logger;

    public IpAllowlistMiddleware(RequestDelegate next, ILogger<IpAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantIpAllowlistStore store,
        ITenantAuthConfigStore authConfigStore,
        IIpAllowlistEvaluator evaluator,
        IFeatureGateService featureGate,
        IAuditService audit)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymousMetadata>() is not null)
        {
            await _next(context);
            return;
        }

        var tenantId = context.User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrEmpty(tenantId))
        {
            await _next(context);
            return;
        }

        if (!featureGate.IsFeatureEnabled(tenantId, PlanFeature.IpAllowlist))
        {
            await _next(context);
            return;
        }

        var role = context.User.FindFirst(ClaimTypes.Role)?.Value;
        var clientIp = context.Connection.RemoteIpAddress;

        if (string.Equals(role, "PlatformAdmin", StringComparison.Ordinal))
        {
            await audit.RecordAsync(
                tenantId: new TenantId(tenantId),
                category: "auth",
                action: "auth.ip_allowlist.bypass",
                severity: "info",
                actorId: context.User.Identity?.Name ?? "unknown",
                actorType: "user",
                metadata: new Dictionary<string, string>
                {
                    ["ip"] = clientIp?.ToString() ?? "unknown",
                    ["path"] = context.Request.Path,
                },
                ct: context.RequestAborted);

            await _next(context);
            return;
        }

        var config = await authConfigStore.GetAsync(tenantId, context.RequestAborted);
        if (config is null || !config.IpAllowlistEnabled)
        {
            await _next(context);
            return;
        }

        var entries = await store.ListAsync(tenantId, context.RequestAborted);

        if (clientIp is null || !evaluator.IsAllowed(clientIp, entries))
        {
            await audit.RecordAsync(
                tenantId: new TenantId(tenantId),
                category: "auth",
                action: "auth.ip_allowlist.denied",
                severity: "warning",
                actorId: context.User.Identity?.Name ?? "unknown",
                actorType: "user",
                metadata: new Dictionary<string, string>
                {
                    ["ip"] = clientIp?.ToString() ?? "unknown",
                    ["path"] = context.Request.Path,
                    ["entry_count"] = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                ct: context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"code":"ip_allowlist_violation"}""", context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
```

- [ ] **Step 9.5: Add the test fake `InMemoryTenantAuthConfigStore`**

If `InMemoryTenantAuthConfigStore` does not already exist in `Storage.InMemory`, check first:

Run: `find src/Asterisk.Platform.Storage.InMemory -name "*TenantAuthConfig*"`

If not present, create `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAuthConfigStore.cs`:

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryTenantAuthConfigStore : ITenantAuthConfigStore
{
    private readonly ConcurrentDictionary<string, TenantAuthConfig> _byTenant = new();

    public Task<TenantAuthConfig?> GetAsync(string tenantId, CancellationToken ct)
        => Task.FromResult(_byTenant.TryGetValue(tenantId, out var c) ? c : null);

    public Task SaveAsync(TenantAuthConfig config, CancellationToken ct)
    {
        _byTenant[config.TenantId] = config;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 9.6: Run tests — confirm they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~IpAllowlistMiddlewareTests"`
Expected: 6 passed, 0 failed.

- [ ] **Step 9.7: Commit**

```bash
git add src/Asterisk.Platform.Api/Middleware/IpAllowlistMiddleware.cs src/Asterisk.Platform.Storage.InMemory/InMemoryTenantAuthConfigStore.cs tests/Asterisk.Platform.Api.Tests/IpAllowlistMiddlewareTests.cs
git commit -m "feat(api): IpAllowlistMiddleware — per-request enforcement

Skips anonymous endpoints; soft-gates on PlanFeature.IpAllowlist
(silent passthrough for unlicensed tenants); honors PlatformAdmin
bypass with audit; resolves config via ITenantAuthConfigStore and
entries via ITenantIpAllowlistStore; emits auth.ip_allowlist.denied
on 403 with the offending ip + path + entry count.

Tests: enabled hit, enabled miss → 403, disabled passthrough,
PlatformAdmin bypass, plan-not-licensed soft-skip, anonymous skip.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.4, §4, §5.2."
```

---

## Task 10: `ManagementTenantIpAllowlistEndpoints` CRUD (TDD)

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/ManagementTenantIpAllowlistEndpoints.cs`
- Create: `tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs`

- [ ] **Step 10.1: Inspect the closest existing CRUD endpoint test**

Run: `find tests/Asterisk.Platform.Api.Tests -name "Management*Tests.cs" | head -5`

Pick the closest match (e.g., `ManagementTenantSettingsEndpointsTests.cs`) for fixture style. The repo's standard pattern uses `WebApplicationFactory` with a `IClassFixture<TestApplicationFactory>` or similar.

- [ ] **Step 10.2: Write failing tests (focus on validation rules unique to this endpoint)**

Create `tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

[Collection("management-api")]
public class ManagementTenantIpAllowlistEndpointsTests : IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient _client;
    private const string TenantId = "t1";

    public ManagementTenantIpAllowlistEndpointsTests(TestApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient(role: "PlatformAdmin");
    }

    [Fact]
    public async Task Post_ShouldReturn201_OnValidCidr()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/management/tenants/{TenantId}/ip-allowlist",
            new AddIpAllowlistEntryRequest("192.0.2.0/24", "office"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        resp.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_ShouldReturn400_OnMalformedCidr()
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/management/tenants/{TenantId}/ip-allowlist",
            new AddIpAllowlistEntryRequest("not-a-cidr", null));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("ip_allowlist_invalid_cidr");
    }

    [Fact]
    public async Task Get_ShouldListEntries()
    {
        await _client.PostAsJsonAsync(
            $"/api/v1/management/tenants/{TenantId}/ip-allowlist",
            new AddIpAllowlistEntryRequest("203.0.113.0/24", "vpn"));

        var resp = await _client.GetAsync($"/api/v1/management/tenants/{TenantId}/ip-allowlist");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IpAllowlistListResponse>();
        body!.Entries.Should().Contain(e => e.Cidr == "203.0.113.0/24");
    }

    [Fact]
    public async Task Delete_ShouldRemoveEntry()
    {
        var add = await _client.PostAsJsonAsync(
            $"/api/v1/management/tenants/{TenantId}/ip-allowlist",
            new AddIpAllowlistEntryRequest("198.51.100.0/24", null));
        add.EnsureSuccessStatusCode();
        var entry = await add.Content.ReadFromJsonAsync<IpAllowlistEntryDto>();

        var resp = await _client.DeleteAsync($"/api/v1/management/tenants/{TenantId}/ip-allowlist/{entry!.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_ShouldReturn400_WhenLastEntryAndEnabled()
    {
        // Seed: enable allowlist with exactly one entry, then attempt to delete it.
        // Test fixture must seed tenant_auth_config.ip_allowlist_enabled = true
        // for this tenant before the request — see TestApplicationFactory.
        // … see fixture helper SeedAllowlistEnabledWithEntry(tenantId, cidr).

        var entryId = await SeedAllowlistEnabledWithEntry(TenantId, "10.0.0.0/8");

        var resp = await _client.DeleteAsync($"/api/v1/management/tenants/{TenantId}/ip-allowlist/{entryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("ip_allowlist_cannot_empty_while_enabled");
    }

    private Task<Guid> SeedAllowlistEnabledWithEntry(string tenantId, string cidr)
    {
        // Implementation note: this helper is added in Task 10.4 to the test
        // fixture as an extension on TestApplicationFactory.
        throw new NotImplementedException("Wire SeedAllowlistEnabledWithEntry in TestApplicationFactory.");
    }
}
```

- [ ] **Step 10.3: Run tests — confirm they fail**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~ManagementTenantIpAllowlistEndpointsTests"`
Expected: 5 fail (`CS0246` on the missing types `AddIpAllowlistEntryRequest`, `IpAllowlistListResponse`, `IpAllowlistEntryDto`).

- [ ] **Step 10.4: Wire `SeedAllowlistEnabledWithEntry` helper in `TestApplicationFactory`**

Locate the test fixture: `find tests/Asterisk.Platform.Api.Tests -name "TestApplicationFactory.cs"`. Add this method directly inside the fixture class (or in a partial extension if the fixture is split):

```csharp
public async Task<Guid> SeedAllowlistEnabledWithEntry(string tenantId, string cidr)
{
    var allowlistStore = Services.GetRequiredService<ITenantIpAllowlistStore>();
    var authConfigStore = Services.GetRequiredService<ITenantAuthConfigStore>();
    var entry = await allowlistStore.AddAsync(tenantId, cidr, null, null, default);
    var existing = await authConfigStore.GetAsync(tenantId, default);
    var config = existing ?? new TenantAuthConfig { TenantId = tenantId };
    config.IpAllowlistEnabled = true;
    await authConfigStore.SaveAsync(config, default);
    return entry.Id;
}
```

If `TestApplicationFactory.Services` is not exposed publicly, add a public `IServiceProvider Services => Server.Services;` accessor. (Mirror the equivalent approach from other admin-endpoint tests in the suite — `grep -rn "public IServiceProvider Services" tests/`.)

- [ ] **Step 10.5: Implement the endpoint group**

Create `src/Asterisk.Platform.Api/Endpoints/ManagementTenantIpAllowlistEndpoints.cs`:

```csharp
using System.Net;
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Asterisk.Platform.Api.Endpoints;

public sealed record IpAllowlistEntryDto(
    Guid Id,
    string Cidr,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed record IpAllowlistListResponse(
    bool Enabled,
    IReadOnlyList<IpAllowlistEntryDto> Entries);

public sealed record AddIpAllowlistEntryRequest(
    string Cidr,
    string? Description);

internal static class ManagementTenantIpAllowlistEndpoints
{
    public static void MapManagementTenantIpAllowlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/management/tenants/{tenantId}/ip-allowlist")
            .RequireAuthorization("system:tenant:configure")
            .RequirePlanFeature(PlanFeature.IpAllowlist);

        group.MapGet("/", List);
        group.MapPost("/", Add);
        group.MapDelete("/{entryId:guid}", Remove);
    }

    private static async Task<IResult> List(
        string tenantId,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        CancellationToken ct)
    {
        var config = await authConfigStore.GetAsync(tenantId, ct);
        var entries = await store.ListAsync(tenantId, ct);
        var dto = new IpAllowlistListResponse(
            Enabled: config?.IpAllowlistEnabled ?? false,
            Entries: entries.Select(e => new IpAllowlistEntryDto(e.Id, e.Cidr, e.Description, e.CreatedAt)).ToArray());
        return Results.Ok(dto);
    }

    private static async Task<IResult> Add(
        string tenantId,
        [FromBody] AddIpAllowlistEntryRequest request,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] IAuditService audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // Pre-validate CIDR up front so the user gets a friendly error
        // instead of a Postgres CHECK violation.
        try
        {
            _ = System.Net.IPNetwork.Parse(request.Cidr);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new ErrorResponse("ip_allowlist_invalid_cidr"));
        }

        var actorId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var entry = await store.AddAsync(tenantId, request.Cidr, request.Description, actorId, ct);

        await audit.RecordAsync(
            tenantId: new TenantId(tenantId),
            category: "auth",
            action: "auth.ip_allowlist.entry_added",
            severity: "info",
            actorId: httpContext.User.Identity?.Name ?? "unknown",
            actorType: "user",
            targetId: entry.Id.ToString(),
            targetType: "IpAllowlistEntry",
            metadata: new Dictionary<string, string>
            {
                ["cidr"] = entry.Cidr,
                ["description"] = entry.Description ?? string.Empty,
            },
            ct: ct);

        var dto = new IpAllowlistEntryDto(entry.Id, entry.Cidr, entry.Description, entry.CreatedAt);
        return Results.Created($"/api/v1/management/tenants/{tenantId}/ip-allowlist/{entry.Id}", dto);
    }

    private static async Task<IResult> Remove(
        string tenantId,
        Guid entryId,
        [FromServices] ITenantIpAllowlistStore store,
        [FromServices] ITenantAuthConfigStore authConfigStore,
        [FromServices] IAuditService audit,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // §4.2 — cannot empty while enabled.
        var config = await authConfigStore.GetAsync(tenantId, ct);
        if (config is { IpAllowlistEnabled: true })
        {
            var count = await store.CountAsync(tenantId, ct);
            if (count <= 1)
                return Results.BadRequest(new ErrorResponse("ip_allowlist_cannot_empty_while_enabled"));
        }

        var removed = await store.RemoveAsync(tenantId, entryId, ct);
        if (!removed)
            return Results.NotFound();

        await audit.RecordAsync(
            tenantId: new TenantId(tenantId),
            category: "auth",
            action: "auth.ip_allowlist.entry_removed",
            severity: "info",
            actorId: httpContext.User.Identity?.Name ?? "unknown",
            actorType: "user",
            targetId: entryId.ToString(),
            targetType: "IpAllowlistEntry",
            ct: ct);

        return Results.NoContent();
    }
}
```

- [ ] **Step 10.6: Run tests — confirm they pass**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~ManagementTenantIpAllowlistEndpointsTests"`
Expected: 5 passed, 0 failed.

- [ ] **Step 10.7: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ManagementTenantIpAllowlistEndpoints.cs tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs
git commit -m "feat(api): ManagementTenantIpAllowlistEndpoints CRUD

GET /api/v1/management/tenants/{id}/ip-allowlist (list + enabled flag)
POST   …/ip-allowlist        (add entry, validates CIDR)
DELETE …/ip-allowlist/{id}   (remove, refuses last while enabled)

Hard-gated by RequirePlanFeature(PlanFeature.IpAllowlist) so
non-licensed tenants get a 403 with plan-upgrade copy. Permission
check 'system:tenant:configure'. Audit emits entry_added/entry_removed
with the actor + cidr.

5 endpoint tests cover happy path POST, malformed CIDR 400,
list, delete, and the cannot-empty-while-enabled 400 flow.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.5, §4."
```

---

## Task 11: Surface `IpAllowlistEnabled` in `ManagementTenantSettingsEndpoints` (TDD)

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/Shared/TenantSettingsDtos.cs` (or wherever `UpdateAuthSettingsDto` lives)
- Modify: `src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs` (the shared `BuildSettingsDto` + `ApplyUpdates`)
- Modify: `tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs` (extend with toggle tests)

- [ ] **Step 11.1: Locate the auth-settings DTOs**

Run: `grep -rln "UpdateAuthSettingsDto" src/Asterisk.Platform.Api/ | head -3`

The current `UpdateAuthSettingsDto` (per spec §3.5) is the place to add the new field. Open it.

- [ ] **Step 11.2: Add `IpAllowlistEnabled` to both the read DTO (`AuthSettingsDto`) and the write DTO (`UpdateAuthSettingsDto`)**

Locate `AuthSettingsDto` (the read shape). Add:

```csharp
public bool IpAllowlistEnabled { get; init; }
```

Locate `UpdateAuthSettingsDto`. Add (as nullable so partial updates remain partial):

```csharp
public bool? IpAllowlistEnabled { get; init; }
```

- [ ] **Step 11.3: Wire the read path**

In `TenantSettingsEndpoints.BuildSettingsDto` (the shared method called by both the tenant-self and management endpoints), find where `AuthSettingsDto` is constructed. Add the new field assignment, e.g.:

```csharp
var auth = new AuthSettingsDto
{
    // ... existing fields
    IpAllowlistEnabled = authConfig?.IpAllowlistEnabled ?? false,
};
```

(Verify the exact constructor shape via `grep -n "new AuthSettingsDto" src/Asterisk.Platform.Api/Endpoints/`.)

- [ ] **Step 11.4: Wire the write path with the §4.1 validation**

In `TenantSettingsEndpoints.ApplyUpdates`, where `UpdateAuthSettingsDto` is consumed, add:

```csharp
if (body.Auth?.IpAllowlistEnabled is bool ipAllowlistEnabled)
{
    if (ipAllowlistEnabled)
    {
        // §4.1 — cannot enable an empty allowlist.
        var ipAllowlistStore = serviceProvider.GetRequiredService<ITenantIpAllowlistStore>();
        var entryCount = await ipAllowlistStore.CountAsync(tenantId, ct);
        if (entryCount == 0)
            return Results.BadRequest(new ErrorResponse("ip_allowlist_enable_requires_entries"));
    }
    config.IpAllowlistEnabled = ipAllowlistEnabled;
    await audit.RecordAsync(
        tenantId: new TenantId(tenantId),
        category: "auth",
        action: ipAllowlistEnabled ? "auth.ip_allowlist.enabled" : "auth.ip_allowlist.disabled",
        severity: ipAllowlistEnabled ? "info" : "warning",
        actorId: actorName,
        actorType: "user",
        ct: ct);
}
```

(The exact wiring depends on `ApplyUpdates`'s actual signature — confirm with `grep -n "ApplyUpdates" src/Asterisk.Platform.Api/Endpoints/TenantSettingsEndpoints.cs`. Inject `ITenantIpAllowlistStore` as a parameter if `ApplyUpdates` accepts a service provider, or extend the parameter list if it accepts injected stores explicitly.)

- [ ] **Step 11.5: Add test for the enable-with-empty-list rejection**

Append to `tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs`:

```csharp
    [Fact]
    public async Task EnableAllowlist_ShouldReturn400_WhenNoEntries()
    {
        // Tenant has zero entries; flipping IpAllowlistEnabled = true must reject.
        var resp = await _client.PutAsJsonAsync(
            $"/api/v1/management/tenants/{TenantId}/settings",
            new ManagementUpdateTenantSettingsRequest(
                Auth: new UpdateAuthSettingsDto { IpAllowlistEnabled = true }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("ip_allowlist_enable_requires_entries");
    }
```

- [ ] **Step 11.6: Run tests**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj --filter "FullyQualifiedName~ManagementTenantIpAllowlistEndpointsTests"`
Expected: 6 passed (5 existing + 1 new), 0 failed.

- [ ] **Step 11.7: Commit**

```bash
git add src/Asterisk.Platform.Api/Endpoints/ tests/Asterisk.Platform.Api.Tests/ManagementTenantIpAllowlistEndpointsTests.cs
git commit -m "feat(api): tenant-settings surface IpAllowlistEnabled toggle

Adds IpAllowlistEnabled to both AuthSettingsDto (read) and
UpdateAuthSettingsDto (write). The toggle endpoint refuses to flip
true when the tenant has zero entries (§4.1) and emits enabled /
disabled audit events on flip.

Test: enable-with-empty-list returns 400 with code
'ip_allowlist_enable_requires_entries'.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.5, §4.1."
```

---

# Phase M — Mechanical wire-up

## Task 12: DI registrations + Program.cs middleware order + ForwardedHeaders

**Files:**
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`
- Modify: `src/Asterisk.Platform.Api/appsettings.json`

- [ ] **Step 12.1: Register `PostgresTenantIpAllowlistStore`**

In `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`, after the `ITenantAuthConfigStore` registration block (around line 94 per the existing pattern), add:

```csharp
services.AddSingleton<ITenantIpAllowlistStore, PostgresTenantIpAllowlistStore>();
```

- [ ] **Step 12.2: Register `InMemoryTenantIpAllowlistStore`**

In `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`, alongside the other in-memory stores:

```csharp
services.AddSingleton<ITenantIpAllowlistStore, InMemoryTenantIpAllowlistStore>();
```

(Locate the existing pattern: `grep -n "AddSingleton<ITenantAuthConfigStore" src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`.)

- [ ] **Step 12.3: Decorate the store with the cache + register the evaluator in Program.cs**

In `src/Asterisk.Platform.Api/Program.cs`, locate the AHH cache decorator block (around line 251 — `AddAuthHotpathCaching` call). Append:

```csharp
// IP Allowlist caching — IMemoryCache decorator over ITenantIpAllowlistStore.
// Mirrors AuthHotpathCaching pattern; cross-replica invalidation is not wired
// because allowlist mutations are operator-driven (rare) and 60 s staleness
// is acceptable per the spec.
builder.Services.AddSingleton<IIpAllowlistEvaluator, DefaultIpAllowlistEvaluator>();
builder.Services.Decorate<ITenantIpAllowlistStore>((inner, sp) =>
    new CachedTenantIpAllowlistStore(inner, sp.GetRequiredService<IMemoryCache>()));
```

If `Scrutor`'s `Decorate` extension is not available in this codebase, use the keyed-singleton pattern that `AddAuthHotpathCaching` uses internally (see `grep -n "AddAuthHotpathCaching" src/Asterisk.Platform.Api/`). Mirror that pattern.

- [ ] **Step 12.4: Register middleware after Authentication**

In `Program.cs`, the middleware ordering block (line ~1170):

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantStatusMiddleware>();
app.UseMiddleware<LicenseGateMiddleware>();
app.UseMiddleware<IpAllowlistMiddleware>();    // NEW — after auth, before endpoints
```

- [ ] **Step 12.5: Map the new endpoint group**

In `Program.cs`, alongside the other `MapManagement*Endpoints()` calls (around line 1275):

```csharp
v1.MapManagementTenantIpAllowlistEndpoints();
```

- [ ] **Step 12.6: Configure ForwardedHeaders**

In `Program.cs`, near the top of the request-pipeline section (BEFORE `app.UseRouting()`), add:

```csharp
// Trust X-Forwarded-For from configured upstream proxies. Default empty →
// no header trust → RemoteIpAddress is the raw socket peer (no behaviour
// change for existing single-node deploys). See spec §4.5.
var trustedProxies = builder.Configuration
    .GetSection("ForwardedHeaders:TrustedProxies")
    .Get<string[]>() ?? Array.Empty<string>();
if (trustedProxies.Length > 0)
{
    var fwdOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                          | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    };
    fwdOptions.KnownNetworks.Clear();
    fwdOptions.KnownProxies.Clear();
    foreach (var cidr in trustedProxies)
    {
        var network = System.Net.IPNetwork.Parse(cidr);
        fwdOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
            network.BaseAddress, network.PrefixLength));
    }
    app.UseForwardedHeaders(fwdOptions);
}
```

- [ ] **Step 12.7: Document the new appsettings section**

In `src/Asterisk.Platform.Api/appsettings.json`, add (or merge into existing root):

```json
"ForwardedHeaders": {
  "TrustedProxies": []
}
```

- [ ] **Step 12.8: Build the whole repo**

Run: `dotnet build -c Release`
Expected: Build succeeds with 0 errors and 0 warnings.

- [ ] **Step 12.9: Commit**

```bash
git add src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs src/Asterisk.Platform.Api/Program.cs src/Asterisk.Platform.Api/appsettings.json
git commit -m "feat(api): wire IpAllowlistMiddleware + ForwardedHeaders config

DI registrations:
- ITenantIpAllowlistStore (Postgres + InMemory)
- IIpAllowlistEvaluator (DefaultIpAllowlistEvaluator)
- CachedTenantIpAllowlistStore decorator (60s TTL)

Middleware: UseMiddleware<IpAllowlistMiddleware>() after auth /
authz / TenantStatus / LicenseGate, before endpoints.

Endpoint mapping: v1.MapManagementTenantIpAllowlistEndpoints().

ForwardedHeaders: opt-in TrustedProxies list in appsettings.json,
empty by default so single-node deploys see no behaviour change.

See docs/specs/2026-05-02-ip-allowlist-design.md §3.6, §4.5."
```

---

## Task 13: Final verification — full suite + AOT compatibility check

**Files:** None modified — verification only.

- [ ] **Step 13.1: Run the full Identity test suite**

Run: `dotnet test tests/Asterisk.Platform.Identity.Tests/Asterisk.Platform.Identity.Tests.csproj -c Release`
Expected: All passing.

- [ ] **Step 13.2: Run the full Postgres-store test suite**

Run: `dotnet test tests/Asterisk.Platform.Storage.Postgres.Tests/Asterisk.Platform.Storage.Postgres.Tests.csproj -c Release`
Expected: All passing.

- [ ] **Step 13.3: Run the full Api test suite**

Run: `dotnet test tests/Asterisk.Platform.Api.Tests/Asterisk.Platform.Api.Tests.csproj -c Release`
Expected: All 882+ tests passing (882 was the baseline; this plan adds ~14 new tests).

- [ ] **Step 13.4: Run the AOT probe**

Run: `dotnet publish tests/Asterisk.Platform.Api.Aot.Probe/ -c Release -r linux-x64 --self-contained`
Expected: Build succeeds with no AOT trim warnings. Any new reflection / `Type.GetType()` would fail this gate per the global CLAUDE.md "AOT-first" rule. The new code uses no reflection (Dapper is reflection-based but is already used pervasively in the repo and pinned via the existing trim allowlist).

- [ ] **Step 13.5: Move the plan file from `active` to `completed`**

```bash
git mv docs/plans/active/2026-05-02-ip-allowlist-implementation.md docs/plans/completed/2026-05-02-ip-allowlist-implementation.md
git commit -m "docs(plans): move IP allowlist plan to completed"
```

- [ ] **Step 13.6: Bump the package version**

In `Directory.Build.props`, bump `<PackageVersion>1.14.6</PackageVersion>` to `<PackageVersion>1.15.0</PackageVersion>` (minor bump for new feature). Commit:

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 1.15.0 — IP allowlist feature

First feature shipped on the v1.3.0 Web roadmap track. SAML SSO and
Compliance Reporting follow as separate specs/plans.
"
```

- [ ] **Step 13.7: Update the project memory**

Add a one-liner to the user's auto-memory `MEMORY.md` (under `~/.claude/projects/-media-Data-Source-IPcom-Asterisk-Platform/memory/`) noting the new endpoint surface and `PlanFeature.IpAllowlist`. Skip if memory infrastructure is not in scope for this run.

---

# Self-Review Checklist (run before declaring plan complete)

- [ ] **Spec coverage** — every section of `docs/specs/2026-05-02-ip-allowlist-design.md` has at least one task implementing it:
  - §1 Architecture → Tasks 4–10 cover the three pieces
  - §2 Data model → Tasks 2 + 3
  - §3.1 Domain → Task 4
  - §3.2 Stores → Tasks 6 + 7
  - §3.3 Cache → Task 8
  - §3.4 Middleware → Task 9
  - §3.5 CRUD endpoints → Task 10
  - §3.6 Plan gate → Task 1 + Task 10 + Task 9
  - §3.7 Audit events → Tasks 9 + 10 + 11 (covers all 6 events listed in spec)
  - §4 Behaviour rules → Tasks 9 (4.3, 4.4, 4.5, 4.6) + 10 (4.2) + 11 (4.1)
  - §5 Testing → Task 5 (5.1) + Task 7 (5.2 Postgres) + Tasks 9 + 10 (5.2 integration)
  - §6 Migration & rollout → Tasks 2 + 3
  - §7 Non-goals → no tasks (intentional)

- [ ] **Placeholder scan** — no "TBD", "TODO", "implement later", or hand-wavy "appropriate error handling" anywhere. Every code block is complete and copy-pasteable.

- [ ] **Type consistency**
  - `IpAllowlistEntry` shape is identical across Tasks 4, 5, 6, 7, 8, 10.
  - `CachedTenantIpAllowlistStore` constructor signature in Task 8 matches the `Decorate` call site in Task 12.3.
  - `AddIpAllowlistEntryRequest` / `IpAllowlistListResponse` / `IpAllowlistEntryDto` shapes in Task 10 match the test in Task 10.2.
  - `IpAllowlistMiddleware.InvokeAsync` parameter list in Task 9.4 matches what the DI container will inject in Task 12.4.

- [ ] **Audit event names** in Task 9 (denied, bypass), Task 10 (entry_added, entry_removed), Task 11 (enabled, disabled) match the six events listed in spec §3.7.

If any item fails review, fix inline and re-check that one item only.
