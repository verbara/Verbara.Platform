---
tier: MEDIANO
owner: Harol A. Reina H.
approver: Harol A. Reina H.
stakeholder: Any operator whose host is not on UTC — which includes this project's own developer machine (UTC-5)
decision_ref: Platform/ADR-0002
---

## Why

**On a host whose local timezone is not UTC, `new DateTimeOffset(value, TimeSpan.Zero)` throws when
`value.Kind == DateTimeKind.Local`** — `System.ArgumentException: The UTC Offset of the local
dateTime parameter does not match the offset argument. (Parameter 'offset')`. The pattern appears
in ~12 Postgres store projections.

Observed live while verifying `encrypt-mfa-secrets-at-rest` on a UTC-5 machine, booting the
published Native AOT binary in `Production` against a real Postgres. Two distinct impacts:

1. **`QueueDistributionWorker` fails every cycle, in a loop.** The log carries
   `fail: Verbara.Platform.Api.Services.QueueDistributionWorker[…] Distribution cycle failed` with
   that exception, repeating for the process lifetime. Conversation distribution is the product's
   core loop.
2. **`POST /api/v1/setup` half-completes and then refuses to retry.** It created the `platform`
   tenant, threw before creating any user, and returned 400. The next attempt returns
   `409 "Platform already initialized."` — because the guard checks for the platform tenant, which
   now exists. **A first-run install on a non-UTC host is left wedged with no documented recovery**
   short of deleting the tenant row by hand.

Setting `TZ=UTC` on the process makes both disappear, which is the diagnostic confirmation.

**Why this matters more than it looks.** The primary product track is the SMB self-host Docker
deployment, installed by operators worldwide — most of them not on UTC. The failure surfaces at
first-run setup, the very first thing an operator does. Containers commonly default to UTC, which
is likely why this has not been reported; it fires on host-run binaries, dev machines, and any
container given a `TZ`.

## What Changes

> ⚠️ **The first two bullets below are SUPERSEDED by [`design.md`](design.md) — read it before
> implementing.** Investigation resolved the "establish why" bullet to a single root cause (the
> process-wide `Npgsql.EnableLegacyTimestampBehavior` switch at
> `Verbara.Platform.Api.csproj:52`), which makes the site-patching approach unnecessary **and
> unsafe**: `DateTime.SpecifyKind(x, DateTimeKind.Utc)` — offered below as one of two equivalent
> options — is the variant that silently *shifts the instant* under legacy semantics. It is
> already corrupting `CreatedAt` at `PostgresBotConfigStore.cs:119`, the repo's only such site.
> The count is also 54 sites, not ~12. Design D1/D3 supersede this with: remove the switch, and
> normalise with `.ToUniversalTime()` at every untrusted ingress. The original text is preserved
> below as the historical record of what was believed when the proposal was approved.

- **Audit every `new DateTimeOffset(x, TimeSpan.Zero)` in the repo** (~12 sites, all in
  `Verbara.Platform.Storage.Postgres` row projections) and make each robust to a `Local`-kind input.
  The mechanical fix is to normalise the `DateTime` to UTC before wrapping — e.g.
  `DateTime.SpecifyKind(x, DateTimeKind.Utc)` when the column is known to hold UTC, or
  `x.ToUniversalTime()` where the kind is genuinely unknown. **Pick one deliberately and apply it
  uniformly**; the two are not equivalent and choosing per-site invites drift.
- **Establish why the kind is `Local` at all.** Npgsql returns `Utc`-kind values for `timestamptz`,
  so a `Local` kind implies a `timestamp without time zone` column, a `DateTime.Now` somewhere
  upstream, or a type-mapping setting. Fix the source if the projection is only the symptom —
  otherwise the same class returns elsewhere.
- **Make `/setup` recoverable.** Whatever the timezone fix, a first-run setup that throws part-way
  must not leave the platform tenant behind such that the endpoint refuses to retry — either make
  it transactional, or make the "already initialized" guard require a platform **user**, not just
  the tenant.
- **Regression coverage that would have caught it:** at least one test that exercises the affected
  projections and the `/setup` path with the process timezone set to something other than UTC.

## Capabilities

### New Capabilities
- `timezone-independent-host`: the Platform host behaves identically regardless of the machine's
  local timezone — no projection, background worker, or first-run setup path may depend on the
  process running in UTC.

### Modified Capabilities
<!-- None. No existing living spec covers timezone handling or first-run setup. -->

## Impact

- **Source:** ~12 `new DateTimeOffset(x, TimeSpan.Zero)` sites across
  `src/Verbara.Platform.Storage.Postgres/Stores/` (AgentCapacity, TenantAddOn, TypificationSchema,
  Dunning, AiSuggestion, TenantAutonomousDisposition, Notification and siblings); possibly the
  Npgsql type-mapping configuration; `SetupEndpoints` for the recoverability half.
- **Tests:** a timezone-varying regression test — note that the existing suite is green precisely
  because CI runners are UTC.
- **Docs:** an operator note if any residual requirement to set `TZ` remains.
- **No schema change. No API contract change. No cross-repo impact.**
  > **Amended by [`design.md`](design.md).** No schema change holds. Two corrections: (a) there IS a
  > wire-format change on non-UTC hosts — `DateTimeOffset` response fields serialise `+00:00`
  > instead of the host offset (same instant, no-op on UTC containers, which is every shipped
  > image); (b) "no cross-repo impact" is only true of the *fix location*. Platform feeds
  > un-normalised `DateTimeOffset` values into `Verbara.Sdk.Pro`'s **compiled** Postgres stores
  > (Dialer, DNC, analytics), so the switch removal does reach across the boundary — but it is
  > fixed entirely Platform-side, with **no Sdk/Pro release required**.

### Out of Scope (explicit)

- **The `localhost` vs `127.0.0.1` tenant-resolution footgun** encountered in the same session —
  tracked separately as `fix-ip-host-tenant-resolution`, since it is a distinct defect with a
  distinct mechanism.
- **A blanket audit of every `DateTime` in the codebase.** This change targets the specific
  `new DateTimeOffset(x, TimeSpan.Zero)` construction that provably throws, plus whatever upstream
  source is found to produce the `Local` kind.
