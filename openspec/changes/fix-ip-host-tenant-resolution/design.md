## Context

See `proposal.md` for motivation. This section records only the constraints that shape the approach —
each one verified against the code, and several of them corrected after an adversarial review of an
earlier draft.

**The resolution order is a fixed sequence in one method.** `TenantResolutionMiddleware.ResolveTenantIdAsync`
(`src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs:75-103`) tries three sources in order
and returns the first hit: webhook path (`/api/webhooks/{tenantId}/{channel}`, first remaining segment
non-blank and not in `ReservedWebhookSegments`) → **subdomain** → `X-Tenant-Id` header.

Two more sources exist that no current document mentions. The JWT `tid` is applied later in
`OnTokenValidated` (`Auth/AuthSchemeConfiguration.cs:89-94`) and only when `Items["TenantId"]` is still
unset — a fallback, never an override — while `ApiKeyAuthenticationHandler.cs:94` overwrites
unconditionally. And for `POST /auth/login` and forgot-password, the **request body outranks everything**
(`Endpoints/AuthEndpoints.cs:85-88` and `:560`: `body > middleware context`).

**The failure has two faces, and only one was reported.** `TenantBoundaryValidationMiddleware:91-97` lets
`Platform` and `Partner` callers past a tenant mismatch — legitimately, since operating on another tenant
is their job — but it does **not** correct `Items["TenantId"]`. On an IP-reached host those callers
therefore never see the 403; they run every read and write against `TenantId("127")`. The 403 in the bug
report is the `Customer` half.

**The exclusion list is case-sensitive.** `ResolveFromSubdomainAsync:113` is an ordinal comparison, so
`WWW.platform.example` resolves tenant `WWW`.

**`Items["TenantId"]` holds a boxed `TenantId` struct, not a string.** `TenantResolutionMiddleware:44`
writes `tenantId.Value`, which is `Nullable<TenantId>.Value` — the struct, not its inner string, despite
reading like the opposite. `AuthSchemeConfiguration:94` and `ApiKeyAuthenticationHandler:94` write
`new TenantId(...)`. Readers type-test the struct (`TenantBoundaryValidationMiddleware:76`,
`TenantStatusMiddleware.cs:21`, and ~35 endpoint `GetTenantId` helpers). That is the convention, and it
constrains how the resolution *source* is carried alongside it (Decision 3).

**Existing test shapes.** `SubdomainResolutionTests.cs:39-69` drives the middleware with a
`DefaultHttpContext` and asserts the resolved tenant — the only shape that can observe *which* tenant
resolved. `TenantResolutionMiddlewareTests.cs:21` already carries an `[InlineData("127.0.0.1")]` row whose
sole assertion is `NotBe(InternalServerError)`, so it stays green either way and pins nothing.

## Goals / Non-Goals

**Goals:**

- Make the resolution *source* a first-class, inspectable fact rather than an implicit consequence of
  statement order — that is what lets the 403 explain itself and what makes the precedence testable
  rather than merely documented.
- Confine every behavioural delta to host strings that cannot name a tenant in this product, so the change
  is provably subtractive on a path that runs on every request.
- Leave one citable home for the precedence decision, so the documents that currently state or imply it
  stop drifting independently.

**Non-Goals:**

- **Not** reworking white-label subdomain resolution or the branding-store lookup.
- **Not** removing the backward-compat fallback for dotted non-IP hostnames — accepted residual, see Risks.
- **Not** changing the webhook-path source, the JWT fallback semantics, or the middleware pipeline order.
  ADR-0031's invariant is untouched.
- **Not** fixing the rate-limiting defects found while scoping this. See Decision 5.
- **Not** touching `Verbara.Platform.Web`'s mirror of the IP bug (`src/core/tenant/resolve-tenant.ts:11-18`),
  masked today by `VITE_DEFAULT_TENANT_ID`. Separate repo, separate change.

## Decisions

### Decision 1 — Guard with `IPAddress.TryParse`, before any label splitting

`ResolveFromSubdomainAsync` returns `null` when `context.Request.Host.Host` parses as an `IPAddress`. The
check goes above the `IndexOf('.')` split, so no label is ever derived from an IP literal.

*Why `IPAddress.TryParse` over a hand-rolled test:* the framework parser is the authority on what an
address literal is, and it accepts the **bracketed** IPv6 form — which matters, because `HostString.Host`
does **not** strip the brackets (`new HostString("[::1]:5000").Host` is `"[::1]"`). An earlier draft of
this design asserted the opposite; it was wrong. Anyone tempted to replace the parser with a hand-rolled
one must preserve bracket acceptance or IPv6 breaks silently.

*What the parser accepts, and why it is safe:* it takes shorthand (`127.1`, `1.2`, `1.2.3`), leading zeros,
and hex/octal labels (`0x7f.0.0.1`). All of those are hosts whose leading label is not a tenant name in
this product, so the guard swallows nothing real. It correctly rejects genuine hostnames (`localhost`,
`acme.platform.example`).

*What the parser rejects, which the earlier draft did not consider:* `127.0.0.1.` (trailing dot),
`999.1.1.1`, and `1.2.3.4.5` all fail `TryParse` and therefore still resolve tenants `127` / `999` / `1`
after the fix. None is reachable by typing a URL — the numeric parse fails and DNS resolution fails — so
only a hand-crafted `Host` header reaches them. A `TrimEnd('.')` before the parse closes the trailing-dot
case for one token and is worth doing; the rest are accepted as unreachable.

*Alternative rejected — fix the IP case by making the header win instead:* that is Decision 2, and it
treats the symptom while leaving `192.168.1.50` resolving tenant `192` for any caller who sends no header.

### Decision 2 — Keep subdomain-over-header precedence; document it, do not invert it

Spec Requirement 3 permits either. This design **keeps the current order**.

*The honest rationale.* An earlier draft argued this on pre-auth security grounds — that inverting would
let a caller override an operator's white-label pin with a header. That argument is weaker than it looked
and is not the reason:

- post-auth, `TenantBoundaryValidationMiddleware` already rejects `Customer` header overrides regardless of
  precedence, and already permits `Platform`/`Partner` ones;
- pre-auth, the login endpoint already accepts a caller-chosen tenant in the request **body**, at higher
  priority than anything the middleware resolved (`AuthEndpoints.cs:85-88`);
- and the rate-limiter partition, the other pre-auth consumer, has never functioned (Decision 5).

So the pre-auth surface the inversion would widen is close to empty. The real reasons to keep the order
are simpler and sufficient: **a white-label subdomain is a deliberate operator statement about which tenant
this host serves**, and **inverting is a live behavioural change for every white-label deployment while
Decision 1 alone changes none**. With the IP guard in place, the header loses only to a *real* subdomain,
which is the intended behaviour.

*Where it gets written down.* The precedence is stated or implied in several live places that have already
drifted. One new **ADR-0038 (tenant-resolution source precedence)** becomes the citable home — and it must
document the full order, including the login-body and JWT/API-key writers above, or it is incomplete on day
one. The live documents become pointers to it:

| File | What is wrong today |
|------|--------------------|
| `CLAUDE.md:115` | "`X-Tenant-Id` header / subdomain wins over JWT `tid`" — true of both against the JWT, silent on header vs subdomain |
| `openspec/config.yaml:19` | same sentence, injected into every future OpenSpec artifact — fixing only `CLAUDE.md` lets this re-seed the gap |
| `.claude/agents/platform-fullstack-expert.md:21` | source list in the wrong order |
| `docs/decisions/0031-...md:15` | wrong order **and** cites ADR-0002 for a precedence ADR-0002 never states |
| `docs/specs/architecture.md:80-82` | order-invariant bullet with no resolution-order companion |
| `.project-memory/project_tenant_architecture.md` | no precedence row at all |

`.project-memory/reference_local_infra_gotchas.md:50` is **not** on that list: it already states the
mechanism completely and correctly (subdomain before header, `"127"` extracted, Customer mismatch → 403,
and the no-dot explanation). An earlier draft claimed it lacked the mechanism; it does not. Once the fix
lands the note becomes redundant, so it is retitled or deleted, and `.project-memory/MEMORY.md:51` follows.

`docs/specs/2026-03-30-tenant-login-resolution-design.md:36-43` already states the real order correctly and
is append-only history; it stays. So do `docs/plans/completed/**`, `docs/operations/**`, `docs/roadmap.md`,
and `openspec/changes/archive/**`. The workspace-root `/media/Data/Source/Verbara/CLAUDE.md` repeats the
ambiguous sentence too, but it is local config outside any git repo — it cannot be a PR task, and the
"stop the drift" claim should not pretend otherwise.

The proposal's `decision_ref` re-points from `Platform/ADR-0002` (which governs tenant *stamping*) to
`Platform/ADR-0038`.

### Decision 3 — Carry the resolution source in `Items["TenantIdSource"]`, written by every writer

Requirement 4 needs the 403 to name *where* the tenant came from, and `TenantBoundaryValidationMiddleware`
runs long after the resolver returned. The source has to be carried, not recomputed — recomputing would
duplicate the precedence logic in a second place, which is the drift this change exists to stop.

`ResolveTenantIdAsync` returns the tenant together with a `TenantSource` enum; the middleware writes the
boxed enum to `Items["TenantIdSource"]` beside `Items["TenantId"]`, under the same non-null guard.

*The enum covers all four writers, not just the middleware.* An earlier draft scoped it to the three
pre-auth sources, which would have shipped a fact that lies: `ApiKeyAuthenticationHandler:94` overwrites
the tenant *after* resolution ran, leaving the source either absent or — worse — stale and pointing at a
subdomain that no longer produced the value. So `TenantSource` is `WebhookPath | Subdomain | Header |
JwtFallback | ApiKeyBinding`, and `AuthSchemeConfiguration:94` and `ApiKeyAuthenticationHandler:94` set it
alongside their writes. The 403 still degrades gracefully to naming the tenant alone when the key is
absent (Requirement 4's second scenario) — a defensive read is cheap and a lying diagnostic is expensive.

*Why a separate `Items` key over a composite value in the existing one:* every reader of `Items["TenantId"]`
type-tests for `TenantId`. Changing the boxed type there would break all of them silently at runtime — a
type test that stops matching fails **open**, not loud. Decision 5 is a live demonstration of exactly that
failure mode. A new key touches nothing that exists; nothing in `src/` enumerates or serialises
`HttpContext.Items`, so no logging enricher, audit path, or telemetry picks the extra key up.

*Allocation:* the boxed enum values are cached as `static readonly object` sentinels, one per enum member,
so the hot path boxes nothing per request.

`TenantSource`'s display strings come from a `switch` expression, not `ToString()` — AOT-safe, and a missed
arm fails the build under `TreatWarningsAsErrors`.

### Decision 4 — Fix the case-sensitive exclusion list in the same change

`www`/`api`/`localhost` become an ordinal-ignore-case comparison. Same defect class, same three lines,
equally subtractive: `WWW` and `API` are not tenant ids in this product either. It carries its own
requirement in the spec rather than riding along undeclared.

### Decision 5 — Record the rate-limiting findings; fix them in their own change

Scoping this change surfaced an independent, security-relevant cluster of defects. The first draft of this
section stated it wrongly, so the corrected version is recorded here in full.

**What is true:** `TenantRateLimitPolicy.ResolvePerTenantTarget:49` reads

```csharp
var tenantId = context.Items.TryGetValue("TenantId", out var val) && val is string s ? s : "__global__";
```

and the `is string` test can never match a boxed `TenantId` struct — the struct's `implicit operator string`
does not apply to a runtime type test. `TenantRateLimitPolicyTests.cs:30` is false-green because its helper
writes a raw string, a type production never produces; the file's own doc header claims those tests pin that
requests do not "collapse to `__global__`", and they pin nothing.

**What the first draft got wrong:** it concluded "every request lands in `__global__` at `Unlimited`". That
method never runs. `RequireRateLimiting` appears **once** in all of `src/`
(`Endpoints/ConversationEndpoints.cs:53`, the `"llm"` policy); there is no `RequireRateLimiting("per-tenant")`,
no `[EnableRateLimiting]`, and `options.GlobalLimiter` is never set — so the registered `"global-safety"`
3000/min net is equally unattached. The real posture is worse than the original claim: **the only operative
rate limit in the API is one route at 30/min.**

**Two further live instances of the same type bug:**

- `TenantRateLimitPolicy.cs:168` (`ResolveTenantKey`, used by the `"llm"` policy) — the `Items` branch is
  dead, so it works only via its raw-header fallback. Callers that resolved by subdomain and send no header
  collapse into the shared bucket.
- `RateLimitHeadersMiddleware.cs:24` — `is not string` → early return, so `X-RateLimit-Limit` /
  `X-RateLimit-Reset` / `X-RateLimit-Tenant` have **never been emitted**, and no test in the repo references
  them.

Plus two design-level issues for that change to settle: the `"llm"` bucket keys off the **unauthenticated**
`X-Tenant-Id` header (the limiter runs before `UseAuthentication`), so an anonymous caller can burn a
victim tenant's AI quota; and `__global__` doubles as sentinel and partition key, so once the type test is
fixed a client sending `X-Tenant-Id: __global__` would land on the `Unlimited` branch — a forgeable bypass.

**Why not here.** This is not a one-line fix. It is three type-test sites, two unattached policies, a
spoofable partition key, and a product decision about which routes get limited and at what ceiling —
switching per-tenant limiting on for the first time in production. Bundling it into a resolution fix would
hide it. Its own change, and by severity it outranks this one.

### Decision 6 — Assert the resolved tenant at the middleware level; assert the 403 end-to-end

Requirement 1's scenarios are about *which tenant resolves*, which only the `DefaultHttpContext` shape can
observe. Those get one case per source plus the IP, dotted-IPv6, uppercase-label, and Platform-principal
cases. Requirement 4's scenarios are about a response body, so they go through
`CrossTenantHeaderAttackFixture`, which already models the authenticated cross-tenant shape.

*Known obstacle:* `CrossTenantHeaderAttackFixture.SendWithSubdomainOverride()` throws `NotSupportedException`
citing a `HostHeaderClient` shim that **does not exist in the repo** (only the misleading comment at
`CrossTenantHeaderAttackFixture.cs:224`). Any subdomain case routed through that fixture must either set
`request.Headers.Host` directly — the shape `TenantResolutionMiddlewareTests` already uses successfully —
or the helper gets repaired. Repairing is preferred; a dead method with a misleading message is a trap.

## Risks / Trade-offs

- **Tenant resolution runs on every request, and a defect here is a cross-tenant data-scoping defect.** →
  Note this is not only a risk *of* the change: for `Platform`/`Partner` callers on an IP-reached host it is
  the **present state**, and the change ends it. The change itself is subtractive on that path — the
  resolver returns `null` for a strictly narrower class of inputs, so a host that resolved tenant *A*
  cannot come to resolve tenant *B*, only nothing, falling through to the next source in the same method.
- **Accepted residual: dotted non-IP hostnames still resolve a phantom tenant.** The backward-compat
  fallback at `TenantResolutionMiddleware.cs:126` turns any unrecognised first label into a tenant id, so
  `verbara.acme-corp.lan`, `platform-api.internal`, and Kubernetes service names all reproduce this bug —
  and there `localhost` is not an available workaround. `X-Forwarded-Host` cannot restore the real host
  either: `Program.cs:1683-1694` enables only `XForwardedFor | XForwardedProto`. Removing the fallback is a
  live behavioural change that `SubdomainResolutionTests.cs:69` deliberately pins, so it needs an operator
  escape hatch (config to disable raw-subdomain fallback) and its own decision. → Recorded in ADR-0038 and
  in the proposal's Out of Scope; follow-up change to be opened, not silently deferred.
- **The behavioural delta is asserted, not assumed.** The naming audit found no seeder, migration, fixture,
  test, compose file, demo script, or doc that creates or expects a numeric tenant id. State it as
  *"nothing in the product uses numeric tenant ids"*, **not** *"the system prevents them"* —
  `SetupEndpoints.IsValidSlug:280-291` permits digits and `ManagementTenantEndpoints.CreateTenant:76-137`
  applies no format validation, so `"999"` is creatable by an admin who tries.
- **One theoretical configuration changes.** A Platform admin could deliberately store branding subdomain
  `"192"`; `192.168.1.50` matches it today and will not after the fix. Nothing in the product does this.
- **Changing `ResolveTenantIdAsync`'s return type touches its call sites.** → It has exactly one
  (`InvokeAsync:41`) and is `private static`; the compiler catches the rest.
- **The 403 body gains caller-influenced content.** → It goes through the existing
  `ApiJsonContext.Default.ErrorResponse` source-gen path (`ApiJsonContext.cs:407`, a single-property record,
  so no new DTO and no source-gen work), JSON-escaped in an `application/json` body, returned only to an
  already-authenticated caller. The current message text appears nowhere else in `src/`, `tests/`, or
  `Verbara.Platform.Web`, so changing it breaks no assertion.
- **Documenting the precedence in several places invites the same drift again.** → ADR-0038 is the single
  source and the rest become pointers. `openspec/config.yaml` matters most: it is injected into every future
  artifact, so a stale sentence there re-seeds the error indefinitely.

## Migration Plan

No schema change, no configuration change, no cross-repo impact. Normal rollout; rollback is a revert of the
one commit. Operators who had adopted the "use `localhost`" workaround keep working unchanged — the
workaround stops being necessary rather than stopping being valid.

## Open Questions

- **Does the 403 name the source as a stable token (`subdomain`) or prose?** Either satisfies Requirement 4;
  the token is friendlier to a future support script. Deferrable — it changes one string literal and no test
  that asserts the *presence* of the tenant and source rather than an exact body.
