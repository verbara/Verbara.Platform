# Changelog

All notable changes to **Asterisk.Platform** are documented here.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) ·
Versioning: [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.9.0] — 2026-04-20 — "Secure + Current" (R3)

Cross-repo coordination: consumes **SDK v1.15.0 + Pro v1.10.0-pro**
(shipped 2026-04-20 as R1 Pre-v2 Foundation). This release closes two P0
security vulnerabilities, lands the foundation layer for observable
resilience, and migrates Platform onto the post-ADR-0029 MIT resilience
primitives.

### Security

- **Impersonation privilege escalation (P0).** `/management/impersonate`
  now verifies the target tenant is in the caller's tenant hierarchy
  (`ParentTenantId` walk, depth-16 cycle protection, fail-closed on
  broken chains). Platform-tenant callers retain their documented
  ability to impersonate any customer tenant; non-platform callers can
  only impersonate themselves or their descendants. Attacks where a
  Tenant A admin issued a JWT for an unrelated Tenant B are now
  rejected with `403 Forbidden` + audit entry.
- **Impersonation audit evasion (P0).** Successful impersonations now
  emit audit entries to **both** the caller tenant (action
  `impersonation_started`, preserved) and the target tenant (new action
  `impersonation_target_accessed`). Target-tenant admins gain full
  visibility of inbound impersonation events.
- **Tenant MFA policy bypass (P0).** `TenantAuthConfig.MfaPolicy` is now
  enforced on all four auth entry points — login, refresh, password
  reset, and user-bound API key authentication. Previously the policy
  was advisory: users with `MfaEnabled=false` could bypass `required_all`
  tenant policies via any of the four paths. Management-type API keys
  (machine-to-machine, `UserId=null`) remain exempt by design. New
  response DTOs `MfaEnrollmentRequiredResponse` and
  `PasswordResetMfaRequiredResponse` signal enrollment/verification
  flows to the frontend.

### Added

- **OpenTelemetry wiring.** `AddAsteriskOpenTelemetry(...)` +
  `AddAsteriskProOpenTelemetry()` + `WithPrometheusExporter()` now
  registered in `Program.cs`. Enrols the full SDK + Pro meter catalog
  (15 SDK meters including the new `Asterisk.Sdk.Resilience` + 15 Pro
  meters) and activity sources. `/metrics` endpoint is now a real
  Prometheus scraping endpoint (was a JSON stub).
- **T27 event bridges** (Pro 1.8.0-pro opt-ins): cluster / conversation
  / agent state transitions now published to `IPushEventBus` via
  `WithClusterEventBridge()` / `WithConversationBridge()` /
  `WithAgentBridge()`. Each bridge throttles per key (100ms cluster /
  50ms conversation / 200ms agent) and captures `Activity.Current` for
  W3C trace propagation.
- **Resilience MVP** — three critical external call-sites now use
  `Asterisk.Sdk.Resilience` keyed policies (pattern matches Pro engine
  precedent):
  - `WebhookDeliveryService` → policy `webhook.delivery` (circuit 5/30s,
    retry 3/500ms, timeout 10s). Wraps per-attempt `HttpClient.SendAsync`
    within the existing 8-attempt user-visible backoff schedule.
  - `SmtpSender` → policy `smtp.send` (circuit 3/60s, retry 2/1s, timeout
    15s). Replaces the hand-rolled `for (attempt = 1..2)` loop.
  - `OidcTokenExchangeService.ExchangeCodeAsync` → policy
    `oidc.token-exchange` (circuit 3/120s, retry 2/500ms, timeout 10s).
    Wraps the token endpoint `PostAsync` only; JWT validation + caching
    intentionally unwrapped.
- New `Asterisk.Platform.Mail.Tests` project (SmtpSender coverage).

### Changed

- **Bot handoff routing.** `WebhookEndpoints.cs` now calls
  `IConversationSwitchboard.TransferToQueueAsync` (drives
  `Active → Escalated → Queued`, releases agent capacity, publishes
  correct state-change event) instead of `AssignToQueueAsync` when the
  bot emits `BotResponse(BotResponseAction.TransferToQueue, queueId)`.
  The previous behavior skipped the `Escalated` transition and broke
  state-machine invariants relied on by downstream analytics and
  supervisor UX.
- **Dependencies**: SDK pinned from `1.11.1` to `1.15.0`; Pro pinned from
  `1.8.1-pro` to `1.10.0-pro` (21 refs). Added explicit
  `Asterisk.Sdk.Resilience` + `Asterisk.Sdk.OpenTelemetry` +
  `Asterisk.Sdk.Pro.OpenTelemetry` pins (previously transitive).

### Removed

- `Asterisk.Sdk.Pro.Resilience` reference. Package was sunset in Pro
  `1.9.0-pro` via ADR-0029 (migration to MIT `Asterisk.Sdk.Resilience`).
  `Program.cs` now uses `Asterisk.Sdk.Resilience.DependencyInjection`
  and `AddAsteriskResilience()`.

### Internal / tests

- Added regression tests pinning tenant-isolation invariants in
  `DefaultConversationService.GetOrCreateForContactAsync` (no production
  change — end-to-end chain was already correctly scoped).
- T27 bridges wiring contract test (`BridgeOptions.DefaultTenantId` +
  `BridgeMetrics` registration).
- 4 impersonation privilege-escalation scenarios (hierarchy check +
  dual audit).
- 10 MFA policy enforcement scenarios across all 4 auth entry points.
- Baseline preserved: **1,669 → 1,699 unit tests** (+30 across 28
  assemblies). 0 warnings, 0 errors.

### Known limitations (flagged for follow-up)

Subagent audits surfaced orthogonal hardening opportunities that are
**not** fixed in this release; each is tracked for a future session:

- JWT signing key persisted as plaintext XML on disk; no key rotation;
  no `jti` claim on impersonation tokens (no replay protection); API key
  `?token=<raw>` query-string fallback risks log leakage.
- OIDC callback (`OidcEndpoints.cs`) does **not** enforce tenant MFA
  policy — users authenticated via external IdP skip the gate.
- `ChangePassword` does **not** require MFA step-up even when policy
  requires MFA — stolen session cookie enables silent password change.
- `MfaPendingCache` / `PasswordResetCache` are in-memory
  `ConcurrentDictionary` instances; MFA challenges are lost on node
  failover in multi-instance deployments. Move to Redis / Pro.Push
  backplane in a later release.

### Asterisk version matrix

Platform continues to run against **Asterisk 22 LTS** (default). Full
smoke validation against **Asterisk 23 Standard** is pending a separate
patch release — the `docker/Dockerfile.asterisk` currently hardcodes
`andrius/asterisk:22` and the codec_opus download URL to the 22.0
series. Parameterizing via `ASTERISK_VERSION` build-arg is tracked for
**v1.9.2** alongside a CI matrix job.

---

## [1.8.1] — 2026-03-31 — "Operations"

Earlier releases are not tracked in this file. Consult
`git log --oneline v1.8.1` for historical context or the roadmap in
[`docs/`](docs/) for milestone summaries.
