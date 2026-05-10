# ADR-0019: Scope-aware management API keys

- **Status:** Accepted
- **Date:** 2026-05-09
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - [ADR-0018 Visibility decision 3 — private now, public on trigger](0018-visibility-decision-3-private-now-public-on-trigger.md)
  - Audit finding: `docs/security/2026-05-09-pre-public-security-review.md` §PREPUB-2026-05-09-ADMIN-002
  - Remediation plan: `docs/plans/active/2026-05-09-trigger-3-p0-p1-remediation-plan.md` §2.3

## Context

The `/management/*` admin surface is double-locked behind `PlatformAdminRequirement(permission)`. Each gate names the seeded RBAC permission it requires (`security.mfa.admin`, `security.jwt.rotate`, `audit.export`, `audit.read`, `retention.read`, `retention.manage`, `tenant.impersonate`, etc.). The host-tenant + Partner-tenant gate is the first lock; the dot-notation permission seed is the second.

The 2026-05-09 pre-public security review found that `PlatformAdminAuthorizationHandler.HandleRequirementAsync` short-circuited the second lock entirely when the principal carried `key_type=management`:

```csharp
// PRE-FIX — PlatformAdminAuthorizationHandler.cs lines 27-32
var keyTypeClaim = context.User.FindFirst("key_type")?.Value;
if (keyTypeClaim == "management")
{
    context.Succeed(requirement);
    return;
}
```

A management API key — issued by `/api/setup` or `POST /management/api-keys` and carrying the blanket `Scopes = ["platform:*"]` per `ManagementApiKeyEndpoints.CreateKey:68` — therefore satisfied every `PlatformAdminRequirement(...)` regardless of which permission was named. The seeded permissions were ornamental for the management-key auth path. The audit cited this as a P1 issue (`PREPUB-2026-05-09-ADMIN-002`) blocking the public-visibility flip per ADR-0018 Trigger 3.

## Decision

We adopt a **scope-aware** model for management API keys:

1. The API-key authentication handler (`ApiKeyAuthenticationHandler`) emits one `scope` claim per entry in `ApiKey.Scopes` so the principal carries the same scope shape as a JWT bearer.
2. `PlatformAdminAuthorizationHandler` keeps the management-key fast-path (no host-tenant/role check) **only** when `requirement.Permission` is `null` — i.e. the surface is gated by the bare `PlatformAdminOnly` policy without a specific permission seed.
3. When `requirement.Permission` is set, the handler enforces it against the principal's `scope` claims. Match rules (mirroring `ApiKey.HasScope`):
    - Exact equality (`scope == required`).
    - Prefix wildcard (`scope ends with ":*"` and the prefix is a prefix of `required`).
    - **Legacy blanket** `platform:*` scope satisfies every `PlatformAdminRequirement(permission)` regardless of the specific permission. This is the back-compat branch and is **deprecated**.
4. New keys SHOULD be issued with explicit, least-privilege scope whitelists (e.g. `["audit.read", "audit.export"]` for an audit-export-only key). The default issuance shape can keep emitting `platform:*` through the v2.0.x patch window for back-compat; downstream consumers updating to scope-aware issuance is non-breaking.

## Back-compat path

| Version | Behaviour for legacy `platform:*` keys | Behaviour for new narrow-scope keys |
|--|--|--|
| **v2.0.x** (this release train; first concrete tag v2.0.1) | Continues to satisfy any `PlatformAdminRequirement(permission)` (back-compat preserved). | Permission must appear (via exact / prefix-wildcard match) in the scope claims. |
| v2.1.0 (next minor, planned) | Same as v2.0.x **plus** a startup / first-use deprecation warning emitted to the audit log on each successful permission-gated call satisfied by the legacy wildcard. | Same as v2.0.x. |
| v3.0.0 (next major, planned) | The legacy `platform:*` blanket is **removed** as a recognised wildcard. Operators must rotate keys to explicit scope whitelists. Removal is a breaking SemVer change and therefore lands at the next major. | Same as v2.0.x. |

A migration runbook will accompany v2.1.0's first release notes. Existing customer integrations (none today — this remediation lands pre-public) can rotate legacy keys to scope whitelists at any time without functional regression.

## Rationale

- **Defends against the documented exploit path.** A leaked or compromised management API key now surfaces only the permissions it was issued with, instead of being a single blanket master credential.
- **Preserves the fast-path for the non-permission-gated surface.** Bare `PlatformAdminOnly` (e.g. `/management/billing/*`, `/management/tenants/*`) continues to accept any management key, so existing operator scripts that hit these surfaces are not broken.
- **Mirrors the existing `ApiKey.HasScope` semantics.** No new scope-matching DSL is introduced. The auth handler and the entity helper agree on what a `platform:*` or `admin:*` scope means.
- **Enables incremental rollout.** The v2.0.x ship preserves the wildcard. v2.1.0 adds visibility (warn). v3.0.0 finishes the migration. No flag-day breakage.
- **Pairs with the bare `PlatformAdminOnly` short-circuit so legacy operator scripts (which hit `/management/billing/*`, `/management/tenants/*` via management keys) keep working.** The scope check applies only to the permission-gated surfaces (`security.jwt.rotate`, `security.mfa.admin`, `audit.export`, `retention.manage`, etc.) — the surfaces the audit explicitly enumerated.

## Consequences

**Positive**
- Closes audit finding `PREPUB-2026-05-09-ADMIN-002` and unblocks ADR-0018 Trigger 3.
- Brings management API keys in line with the documented "double-locked" pattern called out in `Program.cs` policy registrations.
- Aligns the API-key auth handler's claim shape with the JWT handler (both now carry `scope` claims on the principal).

**Negative**
- One additional decision in the v2.1.0 and v3.0.0 release plans (deprecation warning + wildcard removal).
- Legacy customer integrations relying on `platform:*` keys to rotate JWTs / export audits will need to rotate to a narrowly-scoped key by v3.0.0.

**Neutral**
- New scope claim emission is a no-op for code that does not consume it.
- The handler's host-tenant cache and Partner-tenant lookup paths are unchanged.

## References

- `src/Verbara.Platform.Api/Auth/PlatformAdminAuthorizationHandler.cs` — handler with the new scope-aware branch.
- `src/Verbara.Platform.Api/Auth/ApiKeyAuthenticationHandler.cs` — emits per-scope claims.
- `src/Verbara.Platform.Identity/ApiKey.cs` — `HasScope` wildcard semantics (mirrored by the auth handler).
- `tests/Verbara.Platform.Api.Tests/PlatformAdminAuthorizationHandlerTests.cs` — handler-level coverage (succeed/fail/legacy-wildcard).
- `tests/Verbara.Platform.Api.Tests/Endpoints/Security/JwtKeyEndpointsScopeTests.cs` — integration smoke against `JwtKeyEndpoints.RotateKey` (the canary "high-value gate").
