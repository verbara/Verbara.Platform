# Operations — CSAT Runner (customer-satisfaction capture)

**Shipped:** Platform `v2.18.0` (csat-runner, ADR-0020) · **Pro engine:** `Verbara.Sdk.Pro.CsatRunner` `2.9.0-pro` ·
**License gate:** `LicenseFeature.CsatRunner` (HTTP 402 when absent) ·
**Migration:** `016_SurveyCsatExtensions.sql` · **Scope:** digital-first — webchat / email / sms (**voice/TTS deferred**).

## What this is

Verbara.Platform hosts Pro's CSAT orchestrator in-process and persists customer-satisfaction
responses as an additive extension of the existing Surveys domain (**not** a parallel table).
When a conversation from a **CSAT-enabled queue** ends, the orchestrator solicits a rating on the
configured channel; the customer's reply is captured, scored `1..5`, stored on `survey_responses`,
pushed to supervisors, and readable through the per-queue analytics endpoint.

Channels reach the customer differently:

| Channel | Solicit | Capture path |
|--|--|--|
| `webchat` | `csat_requested` system message in the live conversation | `POST /api/v1/csat/responses/webchat` (anonymous, session-token-verified) |
| `email` | dispatch email with a `Reply-To: csat@…` + signed token | `ImapInboundPoller` drains `csat@…`, parses the digit, forwards to `POST /api/v1/csat/responses/email` (internal `X-Service-Key`) |
| `sms` | dispatch SMS + write a `csat_pending_dispatches` row | `CsatSmsCorrelator` matches the inbound reply, forwards to `POST /api/v1/csat/responses/sms` (internal `X-Service-Key`) |
| `voice` | **deferred** — `preview-voice` returns HTTP 501; no TTS ships in this release | — |

Analytics: `GET /api/v1/analytics/csat/queues/{queueId}` (`SupervisorPlus`). Real-time: a
`CsatResponseRecordedEvent` fans out to the `supervisor:{tenantId}` SignalR group on the
`OnCsatResponseRecorded` client method.

> **License.** Every CSAT endpoint (capture + analytics) is gated on `LicenseFeature.CsatRunner`
> and the orchestrator self-gates at runtime. Without the feature, captures return **HTTP 402** +
> RFC 9457 ProblemDetails and no orchestration runs. This is expected on a tenant without the CSAT
> entitlement — it is not an error.

---

## 1. Enable CSAT per queue

CSAT is **off by default** — a queue whose `csat_enabled` is `false` is never solicited. Enabling
is a per-queue config change on `queue_configs` (4 CSAT columns added by migration 016):

| Column | `CsatConfig` property | Meaning |
|--|--|--|
| `csat_enabled` | `Enabled` | Master switch for the queue (default `false`). |
| `csat_channel` | `PreferredChannel` | Preferred capture channel — `voice` / `webchat` / `email` / `sms`; **leave null** to let the engine pick by conversation channel. Do **not** set `voice` (deferred). |
| `csat_prompt_id` | `PromptTemplateId` | Optional `csat_templates.template_id` for the prompt; null falls through the provider chain. |
| `csat_sampling_rate` | `SamplingRatePercent` | Percentage `0..100` of eligible conversations to solicit (CHECK-constrained). Null / 100 = every eligible conversation. |

Set these through the queue admin surface (the queue editor persists `CsatConfig`), or directly:

```sql
UPDATE queue_configs
SET csat_enabled       = true,
    csat_channel       = NULL,   -- engine picks by conversation channel (recommended)
    csat_prompt_id     = NULL,   -- fall through the template provider chain
    csat_sampling_rate = 25      -- solicit 25% of eligible conversations
WHERE tenant_id = :tenant
  AND queue_id  = :queue;
```

**Sampling.** Start low (`10`–`25`) on high-volume queues to avoid survey fatigue, then tune. `0`
disables solicitation without flipping `csat_enabled` off (useful to pause without losing config).

**Eligibility.** Only **digital** conversations (webchat / email / sms) are solicited — a queue set
to `csat_channel = 'voice'` will not produce voice CSAT in this release. Email/SMS solicitation also
requires the customer's address to be resolvable (from the conversation → contact); webchat needs a
live conversation to post the `csat_requested` system message into.

---

## 2. Configure prompt templates

Per-tenant prompt copy lives in `csat_templates`, keyed `(tenant_id, template_id)`, one row per
`(channel, locale)`. Default templates (en-US / es-419 / pt-BR × email / sms / voice) are seeded on
tenant create; overriding is optional. The provider resolves a prompt through the fallback chain
**tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US**, so a
missing locale/channel never fails — it degrades to a default.

Admin CRUD (all `AdminOnly`, all write a `csat`-category audit row):

| Verb | Route | Purpose |
|--|--|--|
| `GET` | `/api/v1/admin/csat/templates/` | List the tenant's templates. |
| `GET` | `/api/v1/admin/csat/templates/{id}` | Fetch one. |
| `PUT` | `/api/v1/admin/csat/templates/{id}` | Upsert (`channel`, `locale`, `subject`, `body`). |
| `DELETE` | `/api/v1/admin/csat/templates/{id}` | Delete (falls back to defaults). |
| `POST` | `/api/v1/admin/csat/templates/{id}/preview-voice` | **HTTP 501** — voice/TTS deferred; shape present, no synthesis. |

Notes:
- `channel` must be one of `voice` / `email` / `sms` (CHECK-constrained). **Webchat uses i18n
  strings, not a per-tenant template** — there is no `webchat` template row.
- `subject` is nullable (SMS has no subject); `body` is required.
- Point a queue at a specific template with `queue_configs.csat_prompt_id = <template_id>`.

---

## 3. Troubleshoot inbound Email (IMAP gap-fill)

The `ImapInboundPoller` (`IHostedService`) drains each configured `csat@…` mailbox on a
~30s `PeriodicTimer`, tracking the **last processed UID per mailbox** so a message is never
double-captured. `CsatReplyMailHandler` parses the rating and forwards it to the internal email
endpoint. Config binds the `Imap` section (`ImapPollerOptions`):

```jsonc
"Imap": {
  "Enabled": true,                 // false ⇒ poller is a no-op
  "PollInterval": "00:00:30",
  "TokenTtl": "7.00:00:00",        // reply-token TTL, must match Pro's dispatcher
  "TokenSigningSecret": "…",       // HMAC-SHA256 secret SHARED with Pro's dispatcher
  "AutoReplyEnabled": false,
  "Mailboxes": [
    { "TenantId": "…", "Host": "imap.example.com", "Port": 993, "UseTls": true,
      "Username": "csat@example.com", "Password": "…", "Folder": "INBOX" }
  ]
}
```

Symptom → cause → fix:

| Symptom | Likely cause | Fix |
|--|--|--|
| No email captures at all | `Imap.Enabled = false`, or no `Mailboxes` configured | Enable + add a per-tenant mailbox. |
| Poller connects but nothing forwards | `TokenSigningSecret` mismatch with Pro's dispatcher (token verify fails) | Set the **same** secret on both sides; check `TokenTtl` = 7 days. |
| Replies past 7 days ignored | Token expired (7-day TTL) | Expected — late replies are not captured. |
| Rating not found in a reply | Digit not in subject or first 200 chars of body (parser scans subject then first 200 body chars for `[1-5]`) | Ask customers to lead with the digit; or rely on the `In-Reply-To` → `csat_pending_dispatches` fallback. |
| Duplicate captures after a restart | Last-UID tracking reset | UID dedup is per-mailbox in-memory; a cold start re-scans above the persisted dispatch window — the `csat_pending_dispatches` consume-once + rating idempotency bound the blast radius. |
| TLS handshake failures | `UseTls`/`Port` mismatch (993 = IMAPS) | `UseTls: true` + `Port: 993` for production; MailHog test brokers use `UseTls: false`. |

The correlation fallback (when the digit alone is ambiguous) matches the message's `In-Reply-To`
header against an open `csat_pending_dispatches` row for the tenant.

---

## 4. Troubleshoot inbound SMS (correlator)

`CsatSmsCorrelator` runs **after** `SmsWebhookHandler`. On a bare `[1-5]` reply it looks up an open
dispatch and forwards the rating; on **any other body it returns false and the message falls through
to normal routing** — so a genuine customer message is never eaten by CSAT.

Lookup (the exact predicate):

```sql
SELECT … FROM csat_pending_dispatches
WHERE tenant_id = :tenant
  AND channel   = 'sms'
  AND correlator = :from_number       -- the sender's phone number
  AND consumed_at IS NULL
  AND sent_at > now() - interval '24 hours'
ORDER BY sent_at DESC;                 -- collision ⇒ most-recent wins
```

Symptom → cause → fix:

| Symptom | Likely cause | Fix |
|--|--|--|
| SMS rating not captured, message routed as normal | No open dispatch within the 24h window for that number | Confirm the dispatcher wrote a `csat_pending_dispatches` row at solicit time (`channel='sms'`, `correlator` = the destination phone in E.164). |
| Rating ignored though a dispatch exists | Reply not a **bare** `1..5` (extra text, or out of range) | Correlator only matches `^\s*[1-5]\s*$`; anything else falls through by design. |
| Wrong survey correlated | Multiple open dispatches to the same number | Most-recent `sent_at` wins; older opens are marked expired without capture. Reduce overlap by lowering `csat_sampling_rate` or the resolicit cadence. |
| Correlator errors on send | `csat_pending_dispatches` params bound without an explicit `NpgsqlDbType` on a nullable/timestamp column | The shipped path sets `NpgsqlDbType.Text`/`.TimestampTz` explicitly — a 42P08 here means a config/DDL drift; re-verify migration 016 applied. |

Number format matters: the `correlator` stored at dispatch and the inbound sender number must match
(normalize both to E.164).

---

## 5. Large-table migration — `CREATE INDEX CONCURRENTLY`

Migration 016 adds two **partial** indexes on `survey_responses`:

```sql
CREATE INDEX idx_survey_resp_queue_captured
    ON survey_responses (tenant_id, queue_name, captured_at DESC)
    WHERE channel IS NOT NULL;

CREATE INDEX idx_survey_resp_agent_captured
    ON survey_responses (tenant_id, agent_id, captured_at DESC)
    WHERE channel IS NOT NULL;
```

The migration runner wraps each file in a **transaction**, and `CREATE INDEX CONCURRENTLY` **cannot
run inside a transaction** — so the migration uses plain `CREATE INDEX`, which takes an
`ACCESS EXCLUSIVE`-blocking `SHARE` lock on `survey_responses` for the duration of the build. On a
small/fresh table this is instant. **On a large existing `survey_responses` (established tenants),
a plain `CREATE INDEX` blocks writes for the whole build** and can stall CSAT capture and survey
inserts.

**Guidance for large tables:** build the two indexes **out-of-band, concurrently, before or
independently of running migration 016** (the migration's `CREATE INDEX IF NOT EXISTS` then becomes
a no-op because the index already exists):

```sql
-- Run OUTSIDE any transaction (psql: not inside BEGIN; not in a transaction-wrapped tool).
-- CONCURRENTLY builds without blocking writes; it is slower and can leave an INVALID index if it fails.
CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_survey_resp_queue_captured
    ON survey_responses (tenant_id, queue_name, captured_at DESC)
    WHERE channel IS NOT NULL;

CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_survey_resp_agent_captured
    ON survey_responses (tenant_id, agent_id, captured_at DESC)
    WHERE channel IS NOT NULL;
```

Then apply migration 016 normally — the column adds are metadata-only (nullable `ADD COLUMN` in
Postgres 11+ does not rewrite the table) and the `CREATE INDEX IF NOT EXISTS` statements no-op.

If a `CONCURRENTLY` build is interrupted it can leave an **`INVALID`** index — check and repair:

```sql
SELECT indexrelid::regclass, indisvalid
FROM pg_index
WHERE NOT indisvalid;
-- If invalid:
DROP INDEX CONCURRENTLY idx_survey_resp_queue_captured;   -- then re-create CONCURRENTLY
```

The other new objects (`csat_pending_dispatches`, `csat_templates`, their indexes, the
`queue_configs` columns) are on new/small tables and need no special handling.

---

## Related

- **Migration:** `src/Verbara.Platform.Storage.Postgres/Migrations/016_SurveyCsatExtensions.sql`
- **Decision:** ADR-0020 (brownfield CSAT extension of the Surveys domain)
- **CHANGELOG:** `[2.18.0]` — full seam + endpoint inventory
- **Pro engine:** `Verbara.Sdk.Pro.CsatRunner` `2.9.0-pro` (orchestrator + the 5 seam contracts + `LicenseFeature.CsatRunner`)
