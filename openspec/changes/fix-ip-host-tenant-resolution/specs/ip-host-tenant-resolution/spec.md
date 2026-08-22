## ADDED Requirements

### Requirement: An IP-address host never yields a tenant from subdomain resolution

`TenantResolutionMiddleware` MUST NOT treat a label of an IP-address literal as a subdomain.
`ResolveFromSubdomainAsync` currently splits the request host on its first `.` and, for `127.0.0.1`,
takes `127` as the subdomain; `127` is not in the `www` / `api` / `localhost` exclusion list, so
resolution returns `TenantId("127")`. The same applies to any dotted IPv4 address — a self-host box
reached at `192.168.1.50` resolves tenant `192`.

The resolver MUST return `null` when the request host parses as an `IPAddress` (v4 or v6), and it
MUST do so **before** any label splitting, so the request falls through to the `X-Tenant-Id` header —
the correct source in exactly this deployment shape.

This is not a loopback convenience. The primary self-host deployment shape is an operator reaching
the box by LAN IP before any DNS exists, and the failure is worse than it first looked: a Customer
principal gets a confusing 403 on everything after a successful login, while a **Platform or Partner
principal is let through and silently scoped to the phantom tenant**.

#### Scenario: A bare IPv4 host honours the tenant header

- **GIVEN** a Platform host reached at `http://127.0.0.1:5199` (or any dotted IPv4 address)
- **AND** a caller holding a valid JWT whose `tid` is `acme`, sending `X-Tenant-Id: acme`
- **WHEN** the caller requests any authenticated endpoint
- **THEN** the resolved tenant is `acme`
- **AND** the response is NOT `403 "Tenant header does not match authenticated principal."`

#### Scenario: A Platform principal on an IP host is scoped to its real tenant

- **GIVEN** a Platform host reached at a bare IPv4 address
- **AND** a caller whose JWT `tid` names a tenant of type `Platform` (or `Partner`), sending a correct
  `X-Tenant-Id`
- **WHEN** the caller requests any authenticated endpoint
- **THEN** the tenant visible to the endpoint is the one the caller named, never a label taken from the host
- **AND** no read or write is scoped to a tenant derived from the host address

> This scenario pins the more severe half of the defect. `TenantBoundaryValidationMiddleware:91-97`
> lets `Platform` and `Partner` callers past a mismatch without correcting `Items["TenantId"]`, so
> before the fix these callers do not see a 403 at all — they operate on the phantom tenant.

#### Scenario: A dotted IPv6 literal host is treated the same way

- **GIVEN** a Platform host reached at an IPv4-mapped IPv6 literal such as `[::ffff:127.0.0.1]`
- **WHEN** tenant resolution runs
- **THEN** subdomain resolution contributes nothing
- **AND** the tenant is taken from the header or the path

> The literal MUST contain a dot. A bracketed literal with no dot (`[::1]`) already returns `null`
> today through the `IndexOf('.') <= 0` early-out, so a scenario written against it would pass before
> and after the fix and pin nothing. `HostString.Host` does **not** strip the brackets — the guard
> works because `IPAddress.TryParse` accepts the bracketed form.

#### Scenario: A real subdomain still resolves

- **GIVEN** a Platform host reached at `acme.platform.example`
- **WHEN** tenant resolution runs
- **THEN** the subdomain path still resolves `acme`, through the branding store where one is mapped
- **AND** the behaviour for hostnames is unchanged by this fix

### Requirement: Reserved host labels are excluded regardless of case

The `www` / `api` / `localhost` exclusion MUST be matched case-insensitively. It is an ordinal
comparison today (`ResolveFromSubdomainAsync:113`), so `WWW.platform.example` resolves `TenantId("WWW")`
— the same defect class as the IP case, on the same three lines, with the same consequence for whoever
is calling.

#### Scenario: An uppercase reserved label resolves no tenant

- **GIVEN** a Platform host reached at `WWW.platform.example` (or `API.platform.example`)
- **WHEN** tenant resolution runs
- **THEN** subdomain resolution contributes nothing
- **AND** the tenant is taken from the header or the path

### Requirement: The precedence between resolution sources is decided and documented

The resolution order MUST be explicit. Today `ResolveFromSubdomainAsync` runs **before** the
`X-Tenant-Id` header, so a subdomain silently wins and the caller cannot override it — which is what
makes the IP case unrecoverable from the client side. That precedence is defensible (a white-label
subdomain should pin the tenant, and inverting it would change live behaviour for every white-label
deployment while the IP guard alone changes none), but it is nowhere stated, and an undocumented
precedence is indistinguishable from an accident when the next person reads it.

This change MUST document the precedence as intentional, in the middleware and in the tenant
architecture notes, with the reason and not only the order. It MUST NOT leave it implicit.

The documented order MUST cover every source that actually participates, including the two that no
current document mentions:

- the **login request body** outranks everything for `POST /auth/login` and forgot-password
  (`AuthEndpoints.cs:85-88`, `:560` — `body > middleware context`);
- the JWT `tid` is a **fallback**, applied in `OnTokenValidated` only when `Items["TenantId"]` is
  unset, while `ApiKeyAuthenticationHandler:94` **overwrites** it unconditionally.

Note that `CLAUDE.md` currently describes the contract as *"`X-Tenant-Id` header / subdomain wins
over JWT `tid`"* — which is true of both against the JWT, but says nothing about header versus
subdomain. Whichever way this resolves, that sentence needs to say so.

#### Scenario: The precedence is stated where a reader will find it

- **GIVEN** the change has landed
- **WHEN** a reader consults the middleware or the tenant-resolution documentation
- **THEN** the order of login body, path, subdomain, header, and JWT fallback is stated explicitly
- **AND** the reason for that order is given, not just the order

### Requirement: A tenant-boundary rejection explains what was resolved

`TenantBoundaryValidationMiddleware`'s 403 MUST name the tenant it resolved and the source it came
from (path, subdomain, or header). Today the body says only that the header does not match the
authenticated principal, which is a dead end: the caller cannot tell that a numeric label from the
host was mistaken for a tenant, and — as observed — sending a correct header changes nothing and
explains nothing.

The caller at this point is **authenticated**, so naming the tenant resolved from their own request
does not disclose another tenant's existence. The message MUST NOT enumerate valid tenants or
confirm whether the resolved tenant exists.

The rejection MUST remain correct when the source is unknown. Not every writer of `Items["TenantId"]`
records a source — `ApiKeyAuthenticationHandler` overwrites the tenant after resolution ran — so the
body MUST degrade to naming the tenant alone rather than asserting a source it cannot know.

#### Scenario: The rejection is diagnosable in one read

- **GIVEN** an authenticated caller whose resolved tenant does not match their principal
- **WHEN** the 403 is returned
- **THEN** the body names the resolved tenant and the source it was resolved from
- **AND** it does not disclose whether that tenant exists, nor enumerate any others

#### Scenario: An unknown source does not produce a false claim

- **GIVEN** a request whose tenant was written by a path that records no resolution source
- **WHEN** a tenant-boundary rejection is returned
- **THEN** the body names the resolved tenant without naming a source
- **AND** it does not attribute the tenant to a source that did not produce it

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs` — tenant resolution runs on
  **every** request and feeds the tenant-boundary check and every store call's tenant scoping. A defect
  here is a cross-tenant data-scoping defect. That is not merely a risk of the change: it is the
  **present state** for `Platform`/`Partner` callers on an IP-reached host, which this change ends.
- `TenantBoundaryValidationMiddleware` (403 body), and the two auth-time writers of `Items["TenantId"]`
  if they are made to record a resolution source.
- **Cross-repo: none.** `Verbara.Platform.Web/src/core/tenant/resolve-tenant.ts:11-18` mirrors the same
  IP defect but is masked by `VITE_DEFAULT_TENANT_ID`; separate repo, separate change.

**Mitigation:**
- The change is subtractive on the risky path: it makes the subdomain resolver return `null` for a
  strictly narrower class of inputs (IP literals, uppercase reserved labels). It cannot cause a host
  that previously resolved tenant *A* to resolve tenant *B* — only to resolve nothing and fall through
  to the header, which is the next source in the same method.
- The middleware order invariant is untouched: `TenantResolutionMiddleware` still runs before
  `UseRateLimiter()` and `UseAuthentication()`, so ADR-0031's contract is not reopened.
- **The rate-limit partition key is unaffected, and not for the reason previously stated here.** An
  earlier draft of this section claimed the partition changes "from `127` to whatever the header says".
  That is false. The `"per-tenant"` policy is registered but attached to **no endpoint**
  (`RequireRateLimiting` appears once in `src/`, for the `"llm"` policy), and its resolver type-tests
  `val is string` against an `Items["TenantId"]` that always holds a boxed `TenantId` struct — so it
  neither runs nor would work if it did. The partition is unchanged by this change under every host.
  Tracked as its own change; see the proposal's Out of Scope.
- Requiring a test per resolution source means the hostname, path, and Platform-principal cases are
  pinned, not just the IP case being fixed.
- The behavioural note is checked rather than assumed: nothing in the product uses numeric tenant ids,
  so nothing can be relying on a numeric label resolving. The product does not *prevent* them —
  `IsValidSlug` permits digits and `CreateTenant` validates no format — it simply never creates one.

**Accepted residual:**
- Dotted **non-IP** hostnames still resolve a phantom tenant through the backward-compat fallback at
  `TenantResolutionMiddleware.cs:126` (`verbara.acme-corp.lan` → `verbara`, a Kubernetes service name →
  its first label), and there the `localhost` workaround is unavailable. `X-Forwarded-Host` cannot
  restore the original host either: `Program.cs:1683-1694` enables only `XForwardedFor | XForwardedProto`.
  Removing the fallback is a live behavioural change that `SubdomainResolutionTests.cs:69` deliberately
  pins, so it needs an operator escape hatch and its own decision. Recorded in ADR-0038; follow-up change
  to be opened.
