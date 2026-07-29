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
the box by LAN IP before any DNS exists, and the resulting symptom is maximally confusing: login
itself succeeds, then every authenticated request returns 403.

#### Scenario: A bare IPv4 host honours the tenant header

- **GIVEN** a Platform host reached at `http://127.0.0.1:5199` (or any dotted IPv4 address)
- **AND** a caller holding a valid JWT whose `tid` is `acme`, sending `X-Tenant-Id: acme`
- **WHEN** the caller requests any authenticated endpoint
- **THEN** the resolved tenant is `acme`
- **AND** the response is NOT `403 "Tenant header does not match authenticated principal."`

#### Scenario: An IPv6 literal host is treated the same way

- **GIVEN** a Platform host reached at an IPv6 literal
- **WHEN** tenant resolution runs
- **THEN** subdomain resolution contributes nothing
- **AND** the tenant is taken from the header or the path

#### Scenario: A real subdomain still resolves

- **GIVEN** a Platform host reached at `acme.platform.example`
- **WHEN** tenant resolution runs
- **THEN** the subdomain path still resolves `acme`, through the branding store where one is mapped
- **AND** the behaviour for hostnames is unchanged by this fix

### Requirement: The precedence between subdomain and header is decided and documented

The resolution order MUST be explicit. Today `ResolveFromSubdomainAsync` runs **before** the
`X-Tenant-Id` header, so a subdomain silently wins and the caller cannot override it — which is what
makes the IP case unrecoverable from the client side. That precedence may well be deliberate (a
white-label subdomain arguably should pin the tenant), but it is nowhere stated, and an undocumented
precedence is indistinguishable from an accident when the next person reads it.

This change MUST either document the precedence as intentional, in the middleware and in the tenant
architecture notes, or invert it. It MUST NOT leave it implicit.

Note that `CLAUDE.md` currently describes the contract as *"`X-Tenant-Id` header / subdomain wins
over JWT `tid`"* — which is true of both against the JWT, but says nothing about header versus
subdomain. Whichever way this resolves, that sentence needs to say so.

#### Scenario: The precedence is stated where a reader will find it

- **GIVEN** the change has landed
- **WHEN** a reader consults the middleware or the tenant-resolution documentation
- **THEN** the order of path, subdomain, and header is stated explicitly
- **AND** the reason for that order is given, not just the order

### Requirement: A tenant-boundary rejection explains what was resolved

`TenantBoundaryValidationMiddleware`'s 403 SHOULD name the tenant it resolved and the source it came
from (path, subdomain, or header). Today the body says only that the header does not match the
authenticated principal, which is a dead end: the caller cannot tell that a numeric label from the
host was mistaken for a tenant, and — as observed — sending a correct header changes nothing and
explains nothing.

The caller at this point is **authenticated**, so naming the tenant resolved from their own request
does not disclose another tenant's existence. The message MUST NOT enumerate valid tenants or
confirm whether the resolved tenant exists.

#### Scenario: The rejection is diagnosable in one read

- **GIVEN** an authenticated caller whose resolved tenant does not match their principal
- **WHEN** the 403 is returned
- **THEN** the body names the resolved tenant and the source it was resolved from
- **AND** it does not disclose whether that tenant exists, nor enumerate any others

## Architectural Risk

**Level:** MEDIUM

**Affected:**
- `src/Verbara.Platform.Api/Middleware/TenantResolutionMiddleware.cs` — tenant resolution runs on
  **every** request and feeds the rate-limiter partition key, the tenant-boundary check, and every
  store call's tenant scoping. A defect here is a cross-tenant data-scoping defect, which is far
  worse than the 403 being fixed.
- `TenantBoundaryValidationMiddleware` if the 403 body changes.
- **Cross-repo: none.**

**Mitigation:**
- The change is subtractive on the risky path: it makes the subdomain resolver return `null` for a
  strictly narrower class of inputs (IP literals). It cannot cause a host that previously resolved
  tenant *A* to resolve tenant *B* — only to resolve nothing and fall through to the header, which
  is already a trusted source at that point in the pipeline.
- The middleware order invariant is untouched: `TenantResolutionMiddleware` still runs before
  `UseRateLimiter()`, so the per-tenant partition resolver still sees a set `Items["TenantId"]` —
  the v2.14.1 bug this repo already paid for is not reopened. Note the partition key for an
  IP-reached host changes from `127` to whatever the header says, which is the intended correction.
- Requiring a test per resolution source means the hostname and path cases are pinned, not just the
  IP case being fixed.
- The behavioural note is checked rather than assumed: no tenant id in this product is a bare
  number, so nothing can be relying on a numeric label resolving — but the change confirms it.
