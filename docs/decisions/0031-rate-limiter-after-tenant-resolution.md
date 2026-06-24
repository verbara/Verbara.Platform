# ADR-0031: Rate limiter must run AFTER tenant resolution (middleware-order invariant)

- **Status:** Accepted
- **Date:** 2026-06-24
- **Deciders:** Verbara maintainer (Harol A. Reina H.)
- **Related:**
  - `src/Verbara.Platform.Api/Program.cs` (middleware pipeline, ~L1543–1562)
  - `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs`
  - `AddPlatformRateLimiting` (per-tenant tier partitioning)
  - Fixed in **Platform v2.14.1** (per-tenant policy was latent/unwired and the ordering collapsed every tenant to `__global__`)
  - Related ADRs: [ADR-0002 (tenant-stamping pipeline end-to-end)](0002-tenant-stamping-pipeline-end-to-end.md), [ADR-0004 (tenant-stamping execution conventions)](0004-tenant-stamping-execution-conventions.md)

## Context

The Platform API rate limiter partitions per tenant: `AddPlatformRateLimiting` builds a partition key from the resolved tenant id (read from `HttpContext.Items["TenantId"]`) so each tenant gets its own throttle bucket and tier. The tenant id is established by `TenantResolutionMiddleware` (header `X-Tenant-Id` / subdomain / JWT `tid`, per ADR-0002), which writes it into `HttpContext.Items` early in the pipeline.

ASP.NET Core's middleware order is the request-handling order. The rate-limiter partition resolver runs **when `UseRateLimiter()` executes in the pipeline**, reading whatever is in `HttpContext.Items` at that instant. If `UseRateLimiter()` runs **before** `TenantResolutionMiddleware`, `Items["TenantId"]` is not yet set, the partition resolver falls back to the shared `__global__` bucket, and **every tenant collapses into one global rate-limit partition** — one noisy tenant throttles all others, and per-tenant tiers never apply. This is silent: requests still succeed under the global ceiling, so the misconfiguration does not surface as an error, only as cross-tenant throttle bleed under load.

A second constraint pulls the opposite direction: rate limiting MUST happen **before** authentication, so abusive traffic is throttled before the (relatively expensive) JWT/API-key validation and DB-backed permission resolution run. Throttling after auth would let an attacker burn auth CPU at the full request rate.

Before v2.14.1 the per-tenant partition policy existed but was effectively latent/unwired, and the ordering did not guarantee tenant resolution preceded the limiter — so the partitioning silently degraded to global.

## Decision

The middleware pipeline order is an **invariant**, pinned with an explanatory comment in `Program.cs`:

```
ErrorHandling → CORS → TenantResolutionMiddleware → UseRateLimiter() → RateLimitHeaders → Authentication → Authorization → TenantBoundaryValidation → ...
```

Specifically, two ordering rules are load-bearing and must never be reordered:

1. **`UseRateLimiter()` MUST run AFTER `TenantResolutionMiddleware`** — so the per-tenant partition resolver sees `HttpContext.Items["TenantId"]` and partitions per tenant instead of collapsing to `__global__`.
2. **`UseRateLimiter()` MUST stay BEFORE `UseAuthentication()`** — so throttling happens before expensive auth work.

`TenantResolutionMiddleware` therefore sits between CORS and the rate limiter. `TenantBoundaryValidationMiddleware` (which validates header/subdomain tenant overrides against the authenticated `tid`) stays AFTER `UseAuthorization()` because it needs `context.User` — it does not affect the rate-limiter partition, which deliberately keys off the pre-auth resolved tenant.

## Consequences

- **Positive:** per-tenant rate-limit partitions and tiers work as designed; one tenant's burst can no longer throttle others. Auth CPU is protected because throttling still precedes authentication.
- **Negative / trade-off:** the partition keys off the **pre-authentication** resolved tenant (header/subdomain/`tid`), which can be supplied by an unauthenticated caller. An attacker could spoof distinct `X-Tenant-Id` values to spread load across many partitions and evade a single bucket. This is accepted: the limiter is one layer; `TenantBoundaryValidationMiddleware` (post-auth) rejects mismatched overrides for actual data access, and IP/global ceilings backstop unauthenticated abuse. Per-IP limiting can be layered if pre-auth partition spraying becomes a problem.
- **Negative (failure mode this guards against):** the bug is silent under light load (global ceiling rarely hit) and only manifests as cross-tenant throttle bleed under contention — making it easy to reintroduce by "tidying" the pipeline. The in-code comment + this ADR are the guardrail; any change to the order of `TenantResolutionMiddleware`, `UseRateLimiter()`, or `UseAuthentication()` MUST cite this ADR.
- **Neutral:** no change to the limiter policy or tiers themselves — only their position in the pipeline relative to tenant resolution.

## Alternatives considered

- **Resolve the tenant inside the rate-limiter partition resolver itself** (re-parse header/subdomain/JWT in the partitioner) — rejected: duplicates `TenantResolutionMiddleware`'s logic, risks divergence (the limiter could partition on a different tenant than the rest of the pipeline trusts), and would need to crack the JWT before `UseAuthentication()`.
- **Run the rate limiter after authentication** (so the authenticated tenant is guaranteed) — rejected: lets unauthenticated/abusive traffic consume auth CPU at full rate, defeating the point of throttling early.
- **Global-only rate limiting (no per-tenant partition)** — rejected: in a multi-tenant SaaS, one tenant must not be able to exhaust the shared budget and degrade every other tenant; per-tenant fairness is a product requirement.
