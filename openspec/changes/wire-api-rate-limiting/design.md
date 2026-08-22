# Design — wire-api-rate-limiting

## Context

See `proposal.md` — *Why* for the defect inventory and `specs/api-rate-limiting/spec.md` for the
requirements. This document covers only how to satisfy them.

Three facts constrain every option below.

**1. `UseRateLimiter()` sits before authentication, by decision.** `Program.cs:1708-1712`:

```
TenantResolutionMiddleware (1708) → UseRateLimiter() (1709) → RateLimitHeadersMiddleware (1710)
  → UseAuthentication() (1711) → UseAuthorization() (1712)
  → TenantBoundaryValidation (1716) → TenantStatus (1717) → LicenseGate (1718) → IpAllowlist (1719)
```

ADR-0031 pins both edges: the limiter after tenant resolution, and before `UseAuthentication()` so
abusive traffic is throttled before expensive auth work. It explicitly **rejected** moving the limiter
after authentication, and explicitly anticipated the remedy this design uses: *"Per-IP limiting can be
layered if pre-auth partition spraying becomes a problem."*

**2. At that position no tenant identifier is verified.** Everything `Items["TenantId"]` can hold pre-auth
comes from the request: header, subdomain, or webhook path. Spec Requirement 3 forbids attributing
unauthenticated traffic to a named tenant's budget on that basis, and its second scenario requires
distinguishing an anonymous flood from the victim tenant's own callers. **That distinction is not
available before `UseAuthentication()` runs.** No amount of care inside the current limiter position can
satisfy it.

**3. `GlobalLimiter` and endpoint policies stack; they do not replace each other.** ASP.NET Core chains
them — the global limiter is acquired first, then any endpoint-specific limiter. So a backstop can be
added without disturbing `.RequireRateLimiting("llm")`, and neither shadows the other.

## Goals / Non-Goals

**Goals**

- Every request bounded, with no per-endpoint opt-in required (Requirement 2).
- Tenant-keyed ceilings applied against a **verified** tenant (Requirements 1, 3).
- One rejection shape, one place, for both stages (Requirement 6).
- ADR-0031's two ordering invariants preserved, not amended away.

**Non-Goals**

- Distributed/cluster-wide counting. Stated as residual in the spec; not solved here.
- New tier values. `RateLimitTier` (Free 60 / Standard 300 / Professional 600 / Enterprise 1200 per
  minute) is the starting point and is not renegotiated by this change.
- Reworking `TenantTierCache`'s population path beyond what Decision 3 requires.

## Decisions

### Decision 1 — Two limiter stages, not one · **PROPOSED, NEEDS APPROVAL**

Split rate limiting into two positions with different jobs and different keys:

| | Stage 1 — *protection* | Stage 2 — *fairness* |
|---|---|---|
| Position | `UseRateLimiter()` at 1709 (unchanged) | new middleware, after `TenantStatusMiddleware` (1717) |
| Key | client IP | verified tenant |
| Purpose | bound total load per source before auth work | per-tenant tier ceiling, LLM cost ceiling |
| Mechanism | `options.GlobalLimiter` | `PartitionedRateLimiter<HttpContext>` in a middleware |
| Satisfies | Req 2 (backstop), Req 3 (anonymous half) | Req 1 (tier ceilings), Req 3 (no forged attribution), Req 4 |

**Why two.** The spec asks for one thing that is only knowable before auth (cheap protection against a
flood that never authenticates) and one thing that is only knowable after it (which tenant this really
is). A single limiter can serve one or the other, never both. ADR-0031 chose the pre-auth position for
the first job and accepted losing the second; this change stops trading them off.

**ADR-0031 is preserved, not amended.** Invariant 1 (limiter after tenant resolution) and invariant 2
(limiter before authentication) both still describe stage 1, which stays exactly where it is. What the
ADR rejected was *moving* the limiter after auth; this **adds** a second one. ADR-0031 gains a
cross-reference, and a correction: its Consequences claim per-tenant partitions "work as designed", which
was never true — the policy was attached to nothing.

**Alternative rejected — one limiter, moved after `UseAuthentication()`.** This is what the ASP.NET Core
documentation recommends ("important to add after `UseAuthentication` because the limiter uses auth
info"), and it would be one middleware instead of two. Rejected because it hands an unauthenticated
flood the full cost of JWT signature validation at line rate, which is the exact scenario ADR-0031's
invariant 2 exists to prevent. The docs' recommendation assumes the limiter's only job is fairness.

**Alternative rejected — one limiter pre-auth, keyed on IP for everything.** Satisfies Requirements 2 and
3 and is the smallest possible change, but abandons Requirement 1: tiers become unenforceable, because an
IP is not a tenant. Free and Enterprise would receive the same ceiling.

### Decision 2 — Stage 1 is the `GlobalLimiter`, keyed on IP, at the existing 3000/min figure

Assign `options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(...)` partitioning on
`context.Connection.RemoteIpAddress`, with the sliding-window shape and the `PermitLimit = 3000` already
written for the unattached `"global-safety"` policy (`TenantRateLimitPolicy.cs:64-69`). `"global-safety"`
is then **deleted**, not left registered — Requirement 2's second scenario forbids a policy that exists
and applies nowhere.

Three reasons for reusing that figure rather than choosing a new one:

- It is the ceiling the author of `"global-safety"` already intended as the instance-wide net. Nothing
  about this change makes it a different number.
- It is a **protection** threshold, not a fairness one. Fairness is stage 2's job. A generous stage 1
  keeps legitimate bursts — E2E runs, load tests, the demo script — well clear of it.
- It degrades safely under the proxy hazard below.

**The proxy hazard, and why the figure absorbs it.** `app.UseForwardedHeaders(...)` is registered at
`Program.cs:1694` **only when** `ForwardedHeaders:TrustedProxies` is non-empty. Behind a reverse proxy
with that key unset, every request carries the proxy's address, and per-IP partitioning collapses to a
single bucket for the whole instance. At 3000/min that degraded state is exactly the instance-wide safety
net `"global-safety"` was written to be — never worse than the status quo intent. It is still wrong, so:
`Program.cs` logs a startup warning when limiting is enforced and no trusted proxies are configured, and
the operator documentation states the dependency.

**Exemptions.** The partitioner returns `RateLimitPartition.GetNoLimiter(...)` for `/health`,
`/health/ready` (`Program.cs:1749`, `:1753`) and the Prometheus scrape path (`:1761`). Kubernetes restarts
pods whose liveness probe fails; a throttled probe is a restart cascade, and ADR-era evidence
(`docs/operations/r55-blk-evidence/`) shows this failure mode has already cost pod restarts once. Health
and metrics are the one place where 429 is more dangerous than unbounded traffic.

### Decision 3 — Stage 2 runs after `TenantStatusMiddleware`, not immediately after authentication

Place the per-tenant middleware at `Program.cs:1717`+, immediately after `TenantStatusMiddleware`.

`TenantStatusMiddleware:76` is what populates `TenantTierCache`. Running stage 2 after it means the
tenant's **real tier** is in hand on the very first request from that tenant on that process. Placing
stage 2 earlier (right after `UseAuthentication()`) would limit the first request of every cold tenant at
the default tier.

**This removes an accepted residual the spec currently carries.** `specs/api-rate-limiting/spec.md`
records "the tier of a tenant's first request on each process is the default" as accepted. Under this
placement it does not occur, and the spec's residual list must be amended rather than left describing a
condition the design eliminates.

**Cost, and why it is bounded.** Stage 2 sits downstream of JWT validation, authorization, the tenant
boundary check and the tenant status lookup — a throttled request has already paid for all of them. That
is acceptable *because* stage 1 caps the rate at which any single source can reach this point. The two
stages are not redundant: stage 1 makes stage 2's late position affordable.

### Decision 4 — Stage 2 is one chained limiter, and it absorbs the `"llm"` policy

Stage 2 holds `PartitionedRateLimiter.CreateChained(tenantTierLimiter, llmLimiter)`:

- **`tenantTierLimiter`** — partitions on the verified tenant, sliding window, `PermitLimit` from the
  tenant's tier. Unlimited tier → `GetNoLimiter`.
- **`llmLimiter`** — returns `GetNoLimiter` unless the endpoint carries an opt-in marker; where it does,
  a 30/min per-tenant bucket that is **not** tier-bypassed (an LLM call costs money at every tier — the
  existing rationale at `TenantRateLimitPolicy.cs:98-102`, which this change keeps).

`.RequireRateLimiting("llm")` at `ConversationEndpoints.cs:53` is replaced by the marker, and the `"llm"`
policy registration is deleted. The framework's endpoint-policy dispatch runs at stage 1's position, so a
policy left there necessarily keys on unverified input — which is the defect, not an implementation
detail. The marker is a typed `Endpoint.Metadata` lookup (`GetMetadata<T>()`), which is AOT-safe: no
reflection, no `MakeGenericType`.

**Alternative rejected — leave `"llm"` attached pre-auth.** It is one line and it works today. Rejected:
it is precisely the case Requirement 3 names. An anonymous caller sending `X-Tenant-Id: <victim>` burns
the victim's paid AI quota with requests that then 401, and stage 1's per-IP ceiling does not stop it —
30 requests is far below any sane per-IP figure.

### Decision 5 — Read `Items["TenantId"]` as `TenantId`, and fail closed

All three broken readers are fixed to unwrap the boxed struct:

| Site | Today | After |
|---|---|---|
| `TenantRateLimitPolicy.cs:49` | `val is string s` | reader deleted with the `"per-tenant"` policy; stage 2 reads the authenticated principal |
| `TenantRateLimitPolicy.cs:168` | `val is string s` | reader deleted with the `"llm"` policy |
| `RateLimitHeadersMiddleware.cs:24` | `is not string` → silent `return` | `is TenantId t` → `t.Value` |

Stage 2 takes its tenant from the **authenticated principal**, not from `Items`, which is what makes it
unforgeable. `Items["TenantId"]` remains the input for reporting (Decision 7) and for
`TenantStatusMiddleware`'s existing use; `TenantBoundaryValidationMiddleware:75-76` already type-tests it
correctly and is the model to follow.

Where a tenant genuinely cannot be determined at stage 2, the request gets the **default tier's ceiling**
in a fallback partition — not `GetNoLimiter`. Requirement 4 makes fail-open a spec violation. The current
code fails open at every one of these sites, which is why the defect survived two releases without a
symptom.

`TenantRateLimitPolicyTests.cs:30` is rewritten to stub with `new TenantId(...)`. Two of its three tests
pass today only because it stubs a type production never writes; written faithfully they fail. Rewriting
the helper is not cosmetic — it is what converts the file from certification into coverage.

### Decision 6 — The reserved partition moves to a namespace no caller can produce

`"__global__"` is today both a sentinel and a partition key: `ResolvePerTenantTarget:52-53` maps that
literal to `Unlimited`, so a client sending `X-Tenant-Id: __global__` would round-trip into
`GetNoLimiter` the moment the type test is fixed. A one-header bypass of all limiting.

Partition keys become **namespaced and disjoint by construction**:

- stage 1: `ip:{address}`
- stage 2, identified: `t:{tenantId}`
- stage 2, unidentified: `t?:` — a key with no tenant component, reachable only by the code path that
  produces it

No caller-supplied value can collide with another namespace, and there is no reserved literal left to
claim. The fix is structural rather than a denylist on the string `"__global__"`, because a denylist has
to be remembered by whoever adds the next sentinel.

### Decision 7 — Headers are emitted at stage 2, and `Unlimited` is never reported as `0`

`RateLimitHeadersMiddleware` moves from `Program.cs:1710` to immediately after stage 2. At its current
position neither the tenant nor its tier is verified, which is a second reason — beyond the type test —
that the headers there could never be trusted. Requirement 5 forbids advertising a ceiling no attached
policy enforces.

`RateLimitTier.Unlimited = 0` with `GetPermitLimit() => (int)tier` yields `X-RateLimit-Limit: 0`,
conventionally read as "no requests permitted" — the exact inverse of the truth. `GetPermitLimit()` is
left alone (it is the enum value and other call sites read it as such); the **header writer** emits the
string `unlimited` for that tier instead of the number. Requirement 5's scenario asks only that the
representation not be a numeric `0` and be unambiguous.

`X-RateLimit-Limit` / `X-RateLimit-Reset` / `X-RateLimit-Tenant` have never reached any client, so there
is no compatibility constraint on their shape — nothing in `src/`, `tests/`, `docker/` or `docs/`
references them outside the middleware itself.

### Decision 8 — One rejection writer, shared by both stages

Stage 2 is custom middleware, so it does not get `options.OnRejected` for free. The body of
`OnRejected` (`TenantRateLimitPolicy.cs:128-149` — 429, `Retry-After`, `ProblemDetails` with
`Type = "rate_limit_exceeded"`, serialized through `ApiJsonContext.Default.ProblemDetails`) is extracted
to a shared static writer that `OnRejected` and stage 2 both call.

Requirement 6 asks for one machine-readable rejection; two hand-written 429 paths would drift. The
`Detail` text becomes stage-aware ("rate limit exceeded" vs "tenant rate limit exceeded") while `Type`
stays a single stable discriminator for clients.

### Decision 9 — Enforcement ships with a measurement mode, defaulting to enforce

`RateLimiting:Mode` = `Enforce` (default) | `Observe`. In `Observe`, a would-be rejection is logged with
the partition key, the stage, and the ceiling — and the request is served.

This exists for the spec's own mitigation: *"Enforcement is introduced against known traffic: the E2E,
load-test and demo paths are measured before the ceilings are declared."* Without an observe mode that
measurement means guessing from logs or shipping 429s to find out.

**The tension is real and is stated rather than hidden:** `Observe` is a way to run with Requirement 2
unenforced. It is mitigated by defaulting to `Enforce`, by logging a startup warning at `Warning` level
whenever the mode is not `Enforce`, and by documenting it as a commissioning tool rather than a
deployment posture. There is no `Off`.

## Risks / Trade-offs

- **Enabling enforcement for the first time produces 429s where traffic has always been accepted.** This
  is the change's principal risk, not the defect it fixes. → Decision 9's `Observe` mode, run first
  against the Playwright suite, the NBomber load tests, `docker/demo/demo-reset.sh` and
  `docker/verbara-smoke-released.sh`; ceilings confirmed against measured peaks before `Enforce` is the
  shipped default in those environments.
- **`Verbara.Platform.Web` has never received a 429 from these routes.** → Cross-repo verification before
  enabling: confirm the shared fetch/TanStack Query layer surfaces 429 and honours `Retry-After` rather
  than treating it as a generic failure or retrying immediately. If it does not, that is a Web-repo
  change that gates enabling, not a follow-up.
- **Per-IP partitioning is only as good as the proxy configuration.** → Decision 2's figure absorbs the
  degraded case; a startup warning names it; operator docs state the `ForwardedHeaders:TrustedProxies`
  dependency.
- **Stage 2 throttles after real work has been done** (auth, authorization, two store lookups). → Bounded
  by stage 1; accepted deliberately as the price of an unforgeable key (Decision 3).
- **Two stages are more moving parts than one.** A future reader can reintroduce the original defect by
  "simplifying" them back together. → ADR-0039 records why both exist, and the in-code comments at both
  positions cite it, mirroring how ADR-0031 guards the ordering.
- **Counting stays per process.** → Accepted residual, already in the spec; published ceilings must state
  their enforcement scope (Requirement 7).

## Migration Plan

1. **ADR-0039** (`docs/decisions/0039-*.md`) records the coverage decision, the two-stage split, the
   ceilings and their enforcement scope. Written first: the proposal's `decision_ref` is re-pointed to it
   afterwards, so the reference is never dangling. ADR-0031 gains the cross-reference and the correction
   to its Consequences section.
2. **Stage 1 + deletions** — `GlobalLimiter` assigned, `"global-safety"` and `"per-tenant"` policies
   deleted, exemptions wired. Behaviour change: a real backstop where there was none.
3. **Stage 2 + `"llm"` migration** — new middleware, chained limiter, endpoint marker;
   `ConversationEndpoints.cs:53` switched from `.RequireRateLimiting("llm")` to the marker; the `"llm"`
   policy registration deleted. The route's ceiling is unchanged at 30/min; only its key becomes
   verified.
4. **Headers + rejection writer** — `RateLimitHeadersMiddleware` moved and fixed, shared 429 writer
   extracted.
5. **Tests** — `TenantRateLimitPolicyTests` rewritten against `TenantId`; first-ever coverage for
   `RateLimitHeadersMiddleware`; an integration test that observes a real `429` with `Retry-After` and the
   problem body; a test that a caller supplying the reserved partition value does not receive its
   treatment.
6. **Measure** — run in `Observe` across E2E, load tests, demo and smoke; compare observed peaks against
   the tier ceilings; adjust the ceilings in ADR-0039 if measurement contradicts them.
7. **Enforce** — flip the default in the shipped configuration; `CHANGELOG.md` `[Unreleased]`.

**Rollback.** Steps 2-4 are revertible as one commit; nothing persists and no schema changes. Between
shipping and full confidence, `RateLimiting:Mode=Observe` disables enforcement without a redeploy of
code, which is the operational rollback for a limit set too low in production.

## Open Questions

- **The stage 1 per-IP figure under a shared NAT.** 3000/min per source address is generous for a single
  client and tight for an office behind one NAT gateway driving a busy contact centre. The measurement
  step (Migration Plan 6) answers it with real numbers; it does not change the design, the spec, or the
  task breakdown either way, so it is deferred rather than guessed.
- **Whether `TenantTierCache` should be invalidated on a tier change.** Today a tier change propagates on
  process restart or whenever `TenantStatusMiddleware` next writes the cache. Stage 2 makes that staleness
  visible in enforcement rather than nowhere. Out of scope here; worth its own follow-up if the cache
  turns out to be long-lived in practice.
