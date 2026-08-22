## Purpose

Bound the request rate the API accepts, per tenant and overall, so that one tenant's traffic — or an
anonymous flood — cannot degrade the service for every other tenant on the instance. The capability covers
which endpoints are protected, how a request is attributed to a bucket, and what a throttled caller is told.

## ADDED Requirements

### Requirement: Declared endpoints enforce a per-tenant request ceiling

The API MUST enforce a per-tenant request ceiling on an **explicitly declared** set of endpoints, and that
set MUST be recorded where an operator can read it. Protection MUST NOT be incidental: an endpoint is
either declared as protected or it is knowingly covered only by the backstop of the next requirement.

Each tenant's consumption MUST be counted against that tenant alone. Exceeding the ceiling MUST NOT
degrade any other tenant's available budget.

The ceiling applied to a tenant MUST be the one its assigned tier defines. A tenant whose tier is not yet
known MUST be limited at the default tier rather than exempted.

#### Scenario: A tenant exceeding its ceiling is throttled

- **GIVEN** a tenant whose tier permits N requests per window on a protected endpoint
- **WHEN** that tenant issues more than N requests within the window
- **THEN** the excess requests are rejected with `429`
- **AND** the rejection carries a `Retry-After` value

#### Scenario: Tenants do not share a bucket

- **GIVEN** two tenants issuing requests to the same protected endpoint
- **WHEN** the first tenant exhausts its ceiling
- **THEN** the second tenant's requests continue to be served
- **AND** the second tenant's remaining budget is unchanged by the first tenant's traffic

#### Scenario: An unknown tier does not mean unlimited

- **GIVEN** a request from a tenant whose tier has not yet been resolved on this instance
- **WHEN** the limit is applied
- **THEN** the default tier's ceiling is used
- **AND** the request is not exempted from limiting

### Requirement: Every request is subject to a backstop ceiling

The API MUST apply a ceiling to **every** request, including requests to endpoints that declare no
per-tenant policy. No route may be unbounded by omission.

This is the requirement the current system fails most plainly: a rate-limit policy that no endpoint
requires does not execute, so registering one is not enforcement. The backstop MUST be wired such that it
applies without any per-endpoint opt-in.

#### Scenario: An endpoint outside the declared set is still bounded

- **GIVEN** an endpoint that declares no per-tenant rate-limit policy
- **WHEN** a caller issues requests to it beyond the backstop ceiling
- **THEN** the excess requests are rejected with `429`

#### Scenario: Registering a policy is not enforcement

- **GIVEN** the API's rate-limit configuration
- **WHEN** an operator audits which ceilings are actually in force
- **THEN** every registered policy is either attached to endpoints or is the backstop
- **AND** no policy exists that is registered and applied nowhere

### Requirement: The partition key MUST NOT be selectable by the caller

A caller MUST NOT be able to choose which rate-limit bucket their request is counted against, nor to
select a more permissive bucket by manipulating request input.

Two specific holes MUST be closed:

- The reserved fallback partition MUST be unreachable from client-supplied values. Any sentinel used to
  mean "tenant unknown" MUST live in a namespace no caller can produce.
- Requests that have not been authenticated MUST NOT be attributed to a named tenant's budget on the
  strength of an unverified request value alone. The rate limiter runs before authentication by a standing
  order invariant (ADR-0031), so any tenant identifier available at that point is caller-supplied. Such
  traffic MUST be partitioned on an identity the caller cannot forge.

#### Scenario: The reserved sentinel cannot be claimed

- **GIVEN** a caller who supplies the reserved fallback partition value as their tenant identifier
- **WHEN** the limit is applied
- **THEN** the request does not receive the fallback partition's treatment
- **AND** it is not exempted from limiting

#### Scenario: An anonymous caller cannot exhaust a named tenant's budget

- **GIVEN** an unauthenticated caller who supplies a victim tenant's identifier
- **WHEN** that caller floods a protected endpoint
- **THEN** the victim tenant's authenticated callers retain their own budget
- **AND** the anonymous traffic is bounded on its own account

### Requirement: An unidentifiable tenant MUST NOT be treated more permissively than an identified one

When the tenant behind a request cannot be determined, the request MUST receive a limit **at least as
strict** as an identified tenant would. Failing to identify the tenant MUST NOT be a path to a larger
budget, and MUST NOT be a path to no limit at all.

This requirement exists because the present implementation fails in exactly that direction: the tenant
lookup reads a type the pipeline never publishes, the test silently fails, and the request falls through to
an unlimited fallback. A limiter that cannot identify its subject must fail **closed**, not open.

#### Scenario: An unresolvable tenant is still limited

- **GIVEN** a request whose tenant cannot be determined at the point the limit is applied
- **WHEN** the caller issues requests beyond the fallback ceiling
- **THEN** the excess requests are rejected with `429`

#### Scenario: A resolved tenant reaches its own partition

- **GIVEN** a request whose tenant has been resolved by the pipeline
- **WHEN** the limit is applied
- **THEN** the request is counted against that tenant's partition
- **AND** it is not counted against the shared fallback partition

### Requirement: The applicable limit is reported to the caller

Responses MUST advertise the ceiling in force for the caller and when it next resets, so a client can pace
itself rather than discovering the limit by being rejected.

An unlimited tier MUST be represented in a way that cannot be read as "no requests permitted". Reporting a
numeric ceiling of `0` for an unlimited tenant is a defect, not a representation choice.

The reported values MUST correspond to a limit that is actually enforced. Advertising a ceiling that no
attached policy applies is worse than advertising nothing.

#### Scenario: A limited caller is told its ceiling

- **GIVEN** a request from a tenant on a tier with a finite ceiling
- **WHEN** the response is returned
- **THEN** it carries the ceiling in force and the time the window resets

#### Scenario: An unlimited tenant is not reported as zero

- **GIVEN** a request from a tenant whose tier is unlimited
- **WHEN** the response is returned
- **THEN** the reported ceiling is not a numeric `0`
- **AND** the representation is unambiguous about the tenant being unlimited

### Requirement: A throttled caller receives a machine-readable rejection

A rejected request MUST return `429` with a `Retry-After` value and a structured problem body naming the
condition, so that clients and operators can distinguish throttling from other failures without parsing
prose.

#### Scenario: The rejection is actionable

- **GIVEN** a caller that has exceeded an enforced ceiling
- **WHEN** the request is rejected
- **THEN** the response status is `429`
- **AND** it carries `Retry-After`
- **AND** the body identifies the condition as a rate-limit rejection in a structured form

### Requirement: The enforcement scope of a documented ceiling is stated

Any ceiling published to operators or tenants MUST state the scope over which it is enforced. Where
counting is per process, the documentation MUST say so, because the effective ceiling for a scaled
deployment is the published figure multiplied by the instance count.

An operator MUST NOT be able to read a published ceiling and reasonably conclude it is a cluster-wide
guarantee when it is not.

#### Scenario: A published ceiling is not mistaken for a cluster guarantee

- **GIVEN** documentation of the ceilings the API enforces
- **WHEN** an operator plans capacity for a multi-instance deployment
- **THEN** the enforcement scope of each ceiling is stated alongside its value

## Architectural Risk

**Level:** HIGH

**Affected:**
- **Every endpoint the coverage decision selects** — this change moves the API from effectively unlimited
  to enforced. The risk is not the defect being fixed; it is the enforcement being switched on.
- The pre-authentication segment of the request pipeline, where the partition key is chosen. Getting this
  wrong converts a protection into an attack surface (a caller who can pick a victim's partition can
  exhaust it).
- Consumers that have never seen a `429` from these routes: `Verbara.Platform.Web`, the Playwright E2E
  suite, the NBomber load tests, and the `docker/demo` and smoke scripts.
- **Cross-repo:** `Verbara.Platform.Web` may need `429` / `Retry-After` handling on routes that have never
  produced one.

**Mitigation:**
- The coverage and the ceilings are a recorded decision with an ADR, not an implementation detail chosen
  in a pull request. The tier ceilings already exist (Free 60 / Standard 300 / Professional 600 /
  Enterprise 1200 per minute) and are the starting point rather than a fresh invention.
- Enforcement is introduced against known traffic: the E2E, load-test and demo paths are measured before
  the ceilings are declared, so a limit is never set below observed legitimate traffic by accident.
- The fail-open direction is closed by an explicit requirement, so the failure mode that hid this defect
  for two releases — an unidentifiable subject silently receiving no limit — becomes a spec violation
  rather than a fallback.
- Coverage must include at least one test that observes a real `429`. The existing suite never does; it
  asserts partition keys through a helper that stubs the seam with a type production does not produce, so
  it is green regardless of whether limiting works.

**Accepted residual:**
- **Counting stays per process.** Buckets are held in process memory, so a scaled deployment enforces the
  published ceiling per instance. Stated rather than solved; distributed counting is a separate change.
- **Traffic bounded only by the backstop is not tier-attributed.** A request rejected before its tenant is
  verified receives the backstop's single ceiling regardless of which tier the caller would have been
  entitled to. This is inherent: the backstop's whole purpose is to bound traffic whose subject is not yet
  known. It is acceptable only while the backstop ceiling stays well above any tier ceiling, so that no
  legitimate tenant meets the backstop before meeting its own limit.
