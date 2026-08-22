## 1. ADR-0038 — tenant-resolution source precedence (first: other artifacts cite it)

- [ ] 1.1 Write `docs/decisions/0038-tenant-resolution-source-precedence.md` documenting the **full**
      order — login request body (`AuthEndpoints.cs:85-88`, `:560`) → webhook path → subdomain →
      `X-Tenant-Id` header → JWT `tid` fallback — and the reason for it (design Decision 2): a white-label
      subdomain is a deliberate operator statement about which tenant the host serves, and inverting is a
      live behavioural change for every white-label deployment while the IP guard alone changes none.
- [ ] 1.2 Record in ADR-0038 that the JWT `tid` is a **fallback** (`OnTokenValidated` writes only when
      `Items["TenantId"]` is unset) while `ApiKeyAuthenticationHandler:94` **overwrites** unconditionally.
      Neither is stated anywhere today, and the API-key case is why the resolution source can go stale.
- [ ] 1.3 Record the accepted residual in ADR-0038: the backward-compat fallback at
      `TenantResolutionMiddleware.cs:126` still turns any unrecognised first label of a dotted non-IP
      hostname into a tenant id (`verbara.acme-corp.lan`, k8s service names), `localhost` is no workaround
      there, and `X-Forwarded-Host` cannot restore the real host because `Program.cs:1683-1694` enables
      only `XForwardedFor | XForwardedProto`. Name the follow-up (operator escape hatch + own decision).
- [ ] 1.4 Fix `docs/decisions/0031-rate-limiter-after-tenant-resolution.md:15`: it lists the sources in the
      wrong order and cites ADR-0002 for a precedence ADR-0002 never states. Point it at ADR-0038.
- [ ] 1.5 Re-point `proposal.md:6` `decision_ref` to `Platform/ADR-0038` — **after** 1.1, so the reference
      is never dangling. *(Already applied in the current draft; verify it survived any artifact edit.)*
- [ ] 1.6 `openspec validate fix-ip-host-tenant-resolution --strict` passes.

## 2. Middleware fix (`TenantResolutionMiddleware.cs`)

- [ ] 2.1 Return `null` from `ResolveFromSubdomainAsync` when the request host parses via
      `IPAddress.TryParse`, **above** the `IndexOf('.')` split (design Decision 1). Keep the bracketed
      IPv6 form working — `HostString.Host` does **not** strip brackets; `TryParse` accepts them. Do not
      replace the parser with a hand-rolled check.
- [ ] 2.2 `TrimEnd('.')` on the host before the parse, so a trailing-dot literal (`127.0.0.1.`) does not
      slip past the guard and resolve tenant `127`.
- [ ] 2.3 Make the `www`/`api`/`localhost` exclusion ordinal-ignore-case (design Decision 4).
- [ ] 2.4 Replace the bare `// Subdomain:` / `// X-Tenant-Id header` comments in `ResolveTenantIdAsync`
      with the precedence **and its rationale**, citing ADR-0038 — spec Requirement 3's scenario demands
      "the reason for that order, not just the order".
- [ ] 2.5 Confirm no behavioural change for hostnames: `acme.platform.example` still resolves `acme`
      through the branding store, and the unknown-subdomain fallback at `:126` still behaves as
      `SubdomainResolutionTests.cs:69` pins it (that residual is explicitly out of scope).

## 3. Resolution source + self-explaining 403

- [ ] 3.1 Add a `TenantSource` enum — `WebhookPath`, `Subdomain`, `Header`, `JwtFallback`,
      `ApiKeyBinding` — and change `ResolveTenantIdAsync` to return the tenant together with its source.
- [ ] 3.2 Write the boxed source to `Items["TenantIdSource"]` beside `Items["TenantId"]`, under the same
      non-null guard. Box once: cache one `static readonly object` sentinel per enum member so the hot
      path allocates nothing. Do **not** change the boxed type in `Items["TenantId"]` — every reader
      type-tests `TenantId` and a broken type test fails open, silently (design Decision 5 is the proof).
- [ ] 3.3 Set the source in the two auth-time writers as well: `AuthSchemeConfiguration.cs:94`
      (`JwtFallback`) and `ApiKeyAuthenticationHandler.cs:94` (`ApiKeyBinding`). The API-key handler
      overwrites the tenant after resolution ran, so without this the recorded source is stale — it would
      name a subdomain that did not produce the value in use.
- [ ] 3.4 Extend `TenantBoundaryValidationMiddleware`'s 403 to name the resolved tenant and its source,
      through the existing `ApiJsonContext.Default.ErrorResponse` path (single-property record — no new
      DTO, no source-gen work). It MUST NOT confirm the tenant exists nor enumerate any other.
- [ ] 3.5 Degrade safely when `Items["TenantIdSource"]` is absent: name the tenant alone rather than
      asserting a source (spec Requirement 4, second scenario).
- [ ] 3.6 Source strings come from a `switch` expression, not `ToString()` — AOT, no reflection; a missed
      enum arm must fail the build under `TreatWarningsAsErrors`.

## 4. Tests

- [ ] 4.1 Middleware-level cases in the `SubdomainResolutionTests` shape (`DefaultHttpContext`, asserting
      the resolved `TenantId`), one per source: webhook path, real subdomain, header.
- [ ] 4.2 A bare IPv4 host (`127.0.0.1` and a LAN address) with a valid `X-Tenant-Id` resolves the header's
      tenant, not a numeric label (spec Requirement 1, first scenario).
- [ ] 4.3 A **dotted** IPv6 literal (`[::ffff:127.0.0.1]`) contributes nothing from the subdomain path.
      Do **not** use `[::1]` — it has no dot, so it already returns `null` today and the test would pass
      before and after the fix, pinning nothing.
- [ ] 4.4 An uppercase reserved label (`WWW.platform.example`) resolves no tenant from the subdomain path
      (spec Requirement 2).
- [ ] 4.5 **Platform-principal case:** a caller whose JWT `tid` names a `Platform` (or `Partner`) tenant,
      on a bare-IPv4 host, is scoped to their real tenant — not to the phantom one. Before the fix this
      caller gets no 403 at all and silently reads and writes under `TenantId("127")`; this is the more
      severe half of the defect and nothing pins it today (spec Requirement 1, second scenario).
- [ ] 4.6 End-to-end through `CrossTenantHeaderAttackFixture`: an authenticated `Customer` on a bare-IPv4
      host sending a correct `X-Tenant-Id` no longer gets `403 "Tenant header does not match authenticated
      principal."`
- [ ] 4.7 A 403 that *is* legitimate names the resolved tenant and its source and discloses nothing else;
      and one whose source is unrecorded names the tenant alone (spec Requirement 4, both scenarios).
- [ ] 4.8 Repair `CrossTenantHeaderAttackFixture.SendWithSubdomainOverride()` — it throws
      `NotSupportedException` citing a `HostHeaderClient` shim that does not exist in the repo (misleading
      comment at `CrossTenantHeaderAttackFixture.cs:224`). Implement it over `request.Headers.Host` (the
      shape `TenantResolutionMiddlewareTests` already uses) or delete it.
- [ ] 4.9 Give `TenantResolutionMiddlewareTests.cs:21`'s existing `127.0.0.1` row a real assertion, or an
      explicit comment that it is a smoke test only and the pinning lives in 4.2.

## 5. Documentation sweep (live documents only)

- [ ] 5.1 `CLAUDE.md:115` — state header vs subdomain, not only "header / subdomain wins over JWT `tid`";
      cite ADR-0038.
- [ ] 5.2 `openspec/config.yaml:19` — same correction. This one is injected into every future OpenSpec
      artifact, so leaving it stale re-seeds the gap indefinitely.
- [ ] 5.3 `.claude/agents/platform-fullstack-expert.md:21` — source list in the correct order.
- [ ] 5.4 `docs/specs/architecture.md:80-82` — add a resolution-order bullet beside the existing
      middleware-order invariant bullet.
- [ ] 5.5 `.project-memory/project_tenant_architecture.md` — add the precedence row; there is none today.
- [ ] 5.6 `.project-memory/reference_local_infra_gotchas.md:50` — this note already states the mechanism
      correctly and completely; it becomes **redundant** once the fix lands, so retitle or delete it (do
      not "add the mechanism" — that premise was wrong). Update `.project-memory/MEMORY.md:51` to match.
- [ ] 5.7 `CHANGELOG.md` `[Unreleased]` — add the entry, following the `fix-local-kind-datetimeoffset`
      shape already there (change name + `decision_ref`). Append-only means do not rewrite past entries;
      it does not mean skip the new one.
- [ ] 5.8 Leave append-only history untouched: `docs/plans/completed/**`,
      `docs/operations/r55-blk-evidence/**`, `docs/specs/2026-03-30-*`, `docs/specs/2026-04-07-*`,
      `docs/roadmap.md`, `openspec/changes/archive/**` — accurate when written. The workspace-root
      `/media/Data/Source/Verbara/CLAUDE.md` repeats the ambiguous sentence but sits outside any git repo,
      so it cannot be a PR change; note it and move on.

## 6. Verification & close

- [ ] 6.1 `dotnet build Verbara.Platform.slnx` clean under `TreatWarningsAsErrors`.
- [ ] 6.2 `dotnet test tests/Verbara.Platform.Api.Tests/` green.
- [ ] 6.3 Live check on the running host: reach it at `http://127.0.0.1:<port>` with a valid JWT and a
      correct `X-Tenant-Id`, confirm the authenticated request succeeds — the exact scenario that returned
      403 while verifying `encrypt-mfa-secrets-at-rest` — and confirm a Platform admin's writes land in
      their real tenant, not in `127`.
- [ ] 6.4 `openspec validate --all --strict` before the PR.

## 7. Follow-ups to open (not implemented here)

- [ ] 7.1 **Rate limiting** (design Decision 5) — its own proposal. Scope it correctly from the start:
      (a) the `"per-tenant"` and `"global-safety"` policies are registered but attached to **no** endpoint
      (`RequireRateLimiting` appears once in `src/`, at `ConversationEndpoints.cs:53` for `"llm"`) and
      `options.GlobalLimiter` is never set — so the only operative limit in the API is one route at
      30/min; (b) the `Items["TenantId"]` `is string` type test is wrong at **three** sites —
      `TenantRateLimitPolicy.cs:49`, `:168`, and `RateLimitHeadersMiddleware.cs:24` (so `X-RateLimit-*`
      headers have never been emitted, untested); (c) `TenantRateLimitPolicyTests.cs:30` is false-green
      because it stubs the seam with a raw string; (d) the `"llm"` bucket keys off the unauthenticated
      `X-Tenant-Id`, letting an anonymous caller burn a victim tenant's AI quota; (e) `__global__` doubles
      as sentinel and partition key, so fixing (b) alone would make `X-Tenant-Id: __global__` a forgeable
      `Unlimited` bypass; (f) the stale XML docs at `TenantRateLimitPolicy.cs:39-45`, `:72-77` assert the
      opposite of all of this.
- [ ] 7.2 **Dotted non-IP hostnames** — the accepted residual from 1.3: an operator escape hatch for the
      backward-compat subdomain fallback.
- [ ] 7.3 **`Verbara.Platform.Web`** — `src/core/tenant/resolve-tenant.ts:11-18` mirrors the IP defect,
      masked by `VITE_DEFAULT_TENANT_ID`. Other repo, own change.
