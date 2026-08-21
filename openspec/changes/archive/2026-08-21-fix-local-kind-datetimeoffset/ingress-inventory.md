# Frozen ingress inventory

Every place an **untrusted** `DateTimeOffset` enters the Platform host and can reach a PostgreSQL
`timestamptz` parameter. Frozen at task 1.3 so the Phase B sweep (§2) and the Phase E CI gate (§5)
are checked against a fixed list rather than a re-grep that drifts as line numbers move.

**Verification command** (must reproduce this list before trusting it):

```sh
grep -rn 'DateTimeOffset\.\(Try\)\?Parse' src/ --include="*.cs"
grep -rn 'FromQuery\] *DateTimeOffset' src/ --include="*.cs"
```

**Totals: 61 sites** — 30 `Parse`/`TryParse` + 24 query parameters + 7 body-DTO properties.

Paths are relative to `src/`. Line numbers verified against the tree at the time this file was
written; treat the *site* as canonical and the line number as a hint.

---

## A. `DateTimeOffset.Parse` / `TryParse` — 30 sites

An offset-less string (`2026-08-20T10:00:00`) parses to the **machine-local** offset, so on a
non-UTC host ordinary client input is already non-zero. This is the largest and least obvious class.

| # | File | Lines | Notes |
|---|------|-------|-------|
| A1–A12 | `Verbara.Platform.Api/Endpoints/AnalyticsEndpoints.cs` | 48, 49, 127, 128, 314, 315, 461, 462, 491, 492, 522, 523 | Feeds **Pro's compiled analytics stores** — cross-boundary read path; a throw here 500s the dashboards. Task 2.1 |
| A13–A18 | `Verbara.Platform.Api/Endpoints/CallAnalyticsEndpoints.cs` | 45, 46, 122, 123, 204, 205 | Same Pro boundary. Task 2.2 |
| A19–A23 | `Verbara.Platform.Api/Endpoints/CampaignEndpoints.cs` | 140, 141, 444, 553, 554 | Campaign create/update + callback scheduling → Pro's compiled `PostgresCampaignStore`. **Write path.** Task 2.3 |
| A24 | `Verbara.Platform.Api/Endpoints/ConversationEndpoints.cs` | 686 | Task 2.4 |
| A25 | `Verbara.Platform.Api/Services/ConversationTimeoutWorker.cs` | 168 | Background loop. Task 2.4 |
| A26 | `Verbara.Platform.Api/Services/CallbackRescueWorker.cs` | 192 | Background loop; already uses `RoundtripKind`. Task 2.4 |
| A27 | `Verbara.Platform.Typification/Validation/DefaultTypificationValidator.cs` | 403 | Discards the result (`out _`) — validation only, no bind. Task 2.4 |
| A28 | `Verbara.Platform.Realtime/Endpoints/AdminRealtimeAuditEndpoint.cs` | 42 | Task 2.4 |
| A29 | `Verbara.Platform.Channels.Email/SimpleEmailParser.cs` | 34 | **Highest-risk single site.** The RFC-2822 `Date:` header is attacker-controlled and routinely carries a non-zero offset (`-0500`). Task 2.5 |

> A29 is one grep hit but the only *externally supplied* offset in the table — every other row is a
> client of the operator's own UI. Counted once; weighted first.

## B. `[FromQuery] DateTimeOffset?` — 16 sites

| File | Lines |
|------|-------|
| `Verbara.Platform.Api/Endpoints/CreditLedgerEndpoints.cs` | 246, 247 |
| `Verbara.Platform.Api/Endpoints/PartnerBillingEndpoints.cs` | 160, 161 |
| `Verbara.Platform.Api/Endpoints/ManagementBillingEndpoints.cs` | 300, 301, 316, 317 |
| `Verbara.Platform.Api/Endpoints/PartnerRevenueEndpoints.cs` | 27, 28, 53, 54 |
| `Verbara.Platform.Api/Endpoints/GdprEndpoints.cs` | 154, 155 |
| `Verbara.Platform.Api/Endpoints/ManagementImpersonationEndpoints.cs` | 493, 494 |

Task 2.6.

## C. Unattributed query parameters — 8 sites

Minimal-API infers these as query parameters without `[FromQuery]`, so a grep for the attribute
misses them. ASP.NET Core binds with `DateTimeStyles.AssumeUniversal`, which only supplies a
**missing** offset — an explicit `-05:00` in the query string survives verbatim.

| File | Lines | Parameter |
|------|-------|-----------|
| `Verbara.Platform.Api/Endpoints/AuditEndpoints.cs` | 25, 26 | `from`, `to` |
| `Verbara.Platform.Api/Endpoints/Audit/AuditEndpoints.cs` | 66, 67, 108, 109 | `from`, `to` (two endpoints) |
| `Verbara.Platform.Api/Endpoints/AuthAdminEndpoints.cs` | 91, 92 | `startDate`, `endDate` |

Task 2.7.

## D. Body DTO properties — 7 sites

`System.Text.Json` deserialises an explicit offset verbatim; there is no `AssumeUniversal`
equivalent. Normalise at the **assignment site**, not in the DTO — the DTOs are source-gen
serialisation contracts and must stay plain.

| DTO (declaration) | Property | Normalise at | Notes |
|---|---|---|---|
| `Dtos/CsatResponseRequest.cs:32` | `CapturedAt` | `Endpoints/CsatResponseEndpoints.cs:237` | **Anonymous public endpoint** — no auth in front of it |
| `Endpoints/CreditLedgerEndpoints.cs:389` | `ExpiresAt` | `CreditLedgerEndpoints.cs:112` | |
| `Endpoints/ManagementBillingEndpoints.cs:744-746` | `EffectiveFrom`, `EffectiveTo` | `ManagementBillingEndpoints.cs:73-74`, `:120-121` | Two call sites |
| `Endpoints/ManagementBillingEndpoints.cs:760` | `PeriodStart`, `PeriodEnd` | `ManagementBillingEndpoints.cs:206` | |
| `Endpoints/DncListEndpoints.cs:279` | `ExpiresAt` | `DncListEndpoints.cs:175-181` | → Pro's compiled DNC store |
| `Endpoints/PartnerBillingEndpoints.cs` (partner rate-card request) | `EffectiveFrom`, `EffectiveTo` | `PartnerBillingEndpoints.cs:70-71`, `:103-104` | **Added after the sweep** — see below |

Task 2.8.

> **Inventory correction.** The last row was MISSED by the original inventory and found during the
> Phase B sweep. The first pass located body DTOs by grepping a hand-picked list of property names,
> which found the management-side `CreateRateCardRequest` but not the partner-side handlers that
> feed the *same* `IRateCardStore.SaveAsync`. The class was then closed systematically instead:
> every `= body.<temporal>` / `= req.<temporal>` assignment in `src/` was enumerated and
> type-checked, and every remaining hit turned out to be `int?`, `string`, `TimeOnly`, or an enum.
> Two `CreatedAt` properties surfaced by a type-level scan (`ContactListDto`, `DncListDto`) are on
> **response** DTOs, not requests — their writes all use `DateTimeOffset.UtcNow`. Section D is
> complete as of that second pass.

---

## Deliberately excluded

- **The 54 `new DateTimeOffset(x, TimeSpan.Zero)` projection sites.** These are *reads*, correct by
  construction once the switch is gone (design D1). Patching them is explicitly out of scope.
- **Server-originated values** — `DateTimeOffset.UtcNow`, `IClock.UtcNow`, and values read back out
  of Postgres. Offset is already zero; normalising is a no-op that only adds noise.
- **`DateTime` (non-offset) parameters.** A different failure mode, and not what this change targets.
