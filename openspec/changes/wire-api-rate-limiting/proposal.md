---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: every tenant on a shared Platform instance; SMB self-host operators exposed to the internet
decision_ref: Platform/ADR-0031
---

## Why

**The Platform API has almost no rate limiting. Not "misconfigured" — unattached.** One route is
throttled. Every other endpoint in the product accepts unbounded request volume from any caller.

ADR-0031 (v2.14.1) moved `UseRateLimiter()` after `TenantResolutionMiddleware` so the per-tenant
partition resolver would stop collapsing every tenant into a shared bucket. That reordering was correct
and is still in place (`Program.cs:1706-1710`). What nobody checked afterwards is whether the policy it
was fixing ever ran. It does not.

### 1. Two policies are registered and attached to nothing

`RequireRateLimiting` appears **exactly once** in all of `src/`:

```
src/Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs:53:            .RequireRateLimiting("llm");
```

There is no `RequireRateLimiting("per-tenant")` anywhere, no `[EnableRateLimiting]` attribute, and
`options.GlobalLimiter` is never assigned. So both of these, defined in
`Middleware/TenantRateLimitPolicy.cs`, are dead on arrival:

- **`"per-tenant"`** (`:78-92`) — the tier-aware sliding window that ADR-0031 exists to protect.
- **`"global-safety"`** (`:64-69`) — a 3000/min net described in the code as exactly that. A named policy
  that no endpoint requires and that is not the `GlobalLimiter` never executes. There is no global net.

**Net production posture: the only operative rate limit in the entire API is `POST
/api/v1/conversations/{id}/typification-suggestion` at 30/min.** Auth endpoints, admin endpoints,
webhooks, exports, search — all unbounded. For a multi-tenant product where one tenant's load lands on
every other tenant's instance, and for an SMB box exposed to the internet, this is the whole point of the
feature missing.

### 2. The tenant lookup is type-wrong in three places, and fails open

`Items["TenantId"]` always holds a **boxed `TenantId` struct**: `TenantResolutionMiddleware:44` writes
`tenantId.Value`, which is `Nullable<TenantId>.Value` — the struct, not its inner string, despite reading
like the opposite — and `AuthSchemeConfiguration:94` / `ApiKeyAuthenticationHandler:94` both write
`new TenantId(...)`. `TenantId`'s `implicit operator string` does **not** apply to a runtime `is string`
type test. So all three of these fail, every time:

| Site | Test | Consequence |
|---|---|---|
| `TenantRateLimitPolicy.cs:49` | `val is string s` | falls back to `"__global__"` → `Unlimited` → `GetNoLimiter`. Latent today (policy unattached); **the moment anyone attaches `"per-tenant"` they ship a silent no-op.** |
| `TenantRateLimitPolicy.cs:168` | `val is string s` | live — the `"llm"` policy. Works only via its raw `X-Tenant-Id` header fallback at `:171-176`; a caller identified by subdomain who sends no header collapses into the shared bucket. |
| `RateLimitHeadersMiddleware.cs:24` | `is not string` → `return` | live — `X-RateLimit-Limit` / `X-RateLimit-Reset` / `X-RateLimit-Tenant` have **never been emitted to anyone**. Nothing in `src/`, `tests/`, `docker/`, or `docs/` references them besides the middleware itself. |

The failure mode is what makes this expensive: a type test that stops matching **fails open**. No
exception, no log, no failing test — just no limiting.

### 3. The tests certify the opposite

`tests/Verbara.Platform.Api.Tests/TenantRateLimitPolicyTests.cs:30` stubs the seam with the wrong type:

```csharp
context.Items["TenantId"] = tenantId;   // a raw string; production writes a boxed TenantId
```

Two of its three tests are green **only** because of that. Written faithfully with
`new TenantId("tenant-a")`, both would fail. The file's own doc header (`:14-19`) states these tests exist
to pin that requests do not "collapse to `__global__`". They pin nothing. `RateLimitHeadersMiddleware`
has no tests at all.

The code comments compound it: `TenantRateLimitPolicy.cs:39-45`, `:71-77` and `:151-164` assert that
`Items["TenantId"]` is "a string", that it "IS populated at this pipeline position", and that "each such
tenant gets its own partition". None of that is true.

### 4. Two design defects that a naive fix would ship

- **The `"llm"` bucket is keyed on unauthenticated input.** `UseRateLimiter()` runs before
  `UseAuthentication()` by ADR-0031's second invariant, so `ResolveTenantKey`'s header fallback reads an
  `X-Tenant-Id` nobody has verified. An anonymous caller can spoof a victim tenant's id and burn that
  tenant's 30/min AI-suggestion quota with requests that then 401. ADR-0031 accepted "partition spraying"
  (spreading load across many partitions to evade one bucket); this is the inverse and more damaging case
  — targeted exhaustion of a *specific* victim's paid quota — and it is not covered by that acceptance.
- **`"__global__"` is both sentinel and partition key.** `ResolvePerTenantTarget:52-53` maps the literal
  `"__global__"` to `Unlimited`. Fix the type test alone and a client sending `X-Tenant-Id: __global__`
  round-trips into that branch: a one-header, client-forgeable bypass of all limiting.

### 5. Smaller, but wrong

- `RateLimitTier.Unlimited = 0` and `GetPermitLimit()` returns `(int)tier`, so an Unlimited tenant would
  advertise `X-RateLimit-Limit: 0` — conventionally read as "no requests permitted", the opposite of the
  truth.
- `TenantTierCache` is a per-process `ConcurrentDictionary` populated by `TenantStatusMiddleware:76`,
  which runs **after** `UseRateLimiter()`. The first request from a tenant on each process therefore
  limits at the default `Standard`, not the tenant's real tier. Buckets are also per-process, so limits
  multiply by instance count in any scaled deployment.

## What Changes

- **Decide and record the coverage.** Which endpoint groups carry `"per-tenant"`, and whether a real
  `options.GlobalLimiter` replaces the unattached `"global-safety"` policy. This is the product decision
  at the centre of the change and it gets a new ADR; the ceilings in `RateLimitTier` (Free 60 / Standard
  300 / Professional 600 / Enterprise 1200 per minute) are already chosen and are the starting point.
- **Attach the policies**, so the wiring is exercised rather than asserted.
- **Fix the three type tests** by unwrapping the boxed `TenantId` — and make the readers accept the struct
  as the canonical form rather than continuing to expect a string.
- **Make `"__global__"` unforgeable.** The sentinel must not be reachable from client-supplied input.
- **Settle the pre-auth partition posture** for both policies: partitioning on an unverified header is
  spoofable by construction, so unauthenticated traffic needs an identity the caller cannot choose
  (per-IP is the obvious candidate) even if authenticated traffic stays per-tenant.
- **Emit the `X-RateLimit-*` headers correctly**, including a truthful representation of the Unlimited
  tier.
- **Replace the false-green tests** with coverage that fails if the wiring regresses — including at least
  one integration test that actually observes a `429`, which the existing suite never does.
- **Correct the XML documentation** on `TenantRateLimitPolicy` and `RateLimitHeadersMiddleware`, which
  currently describes behaviour the code does not have.

## Capabilities

### New Capabilities
- `api-rate-limiting`: the API enforces per-tenant request ceilings on the endpoints it declares, with a
  partition key no caller can forge, and reports the applicable limit back to the caller.

### Modified Capabilities
<!-- None. No living spec covers rate limiting today; ADR-0031 governs the middleware order only. -->

## Impact

- **Source:** `src/Verbara.Platform.Api/Middleware/TenantRateLimitPolicy.cs`,
  `Middleware/RateLimitHeadersMiddleware.cs`, `Services/TenantTierCache.cs`, `Program.cs` (limiter
  registration and any `GlobalLimiter`), plus a `RequireRateLimiting` call on every endpoint group that
  the coverage decision selects.
- **Tests:** `TenantRateLimitPolicyTests` rewritten against the production type; new coverage for
  `RateLimitHeadersMiddleware` (currently zero); at least one 429 integration test.
- **Docs:** a new ADR for coverage and ceilings; ADR-0031 gains a note that its intent was not realised
  until this change; `CHANGELOG.md` `[Unreleased]`.
- **Runtime behaviour: this is the change's real risk.** Turning limiting on for the first time can
  produce 429s for traffic that has always been accepted — including the Web app, E2E suites, load tests,
  and the demo and smoke scripts. The rollout has to account for that rather than discover it.
- **No schema change.** Cross-repo: `Verbara.Platform.Web` may need to handle 429 and `Retry-After` on
  routes that never returned them; verify before enabling.

### Out of Scope (explicit)

- **Cluster-wide (distributed) rate limiting.** Buckets stay per-process. Worth stating as a known
  limitation with its own follow-up, not solving here.
- **`fix-ip-host-tenant-resolution`.** Independent and compatible: that change adds a *new*
  `Items["TenantIdSource"]` key and deliberately does not alter the boxed type in `Items["TenantId"]`,
  which is exactly the type this change teaches the three readers to expect. Either can land first.
- **Billing-driven quota enforcement.** `Verbara.Platform.Billing` metering and quotas are a separate
  mechanism; this change is about request-rate protection, not usage accounting.
