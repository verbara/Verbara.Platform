# AgentAssist Runtime Setup

**Audience:** Platform operators enabling or rotating the AI provider
powering AgentAssist for a tenant. Shipped in Platform v1.10.0 + Pro
v1.12.0-pro (R5.1 Task J).

## What the runtime toggle does

`Asterisk.Sdk.Pro.AgentAssist` exposes `IAgentAssistFeatureToggle`, a
consumer-supplied gate that the `AgentAssistEngine` consults at the
start of every call session. When the toggle returns `false`, the
engine:

1. Emits `agentassist.session.skipped` counter (`Asterisk.Sdk.Pro.AgentAssist` meter).
2. Returns immediately — no provider is started, no tokens are consumed,
   no audio snoop is opened.

Platform v1.10.0 registers an always-present Platform-backed
implementation of this toggle so operators can flip AgentAssist on or off
**without a redeploy** and can rotate provider credentials without
restarting the API node.

## Endpoint surface

Routes live under `/api/v1/admin/features/agent-assist` and require the
`features:agent-assist:manage` permission (seeded into the
`platform_admin` role template — see **RBAC hot-reload** below).

| Verb | Path | Body | Returns |
|------|------|------|---------|
| `GET`  | `/api/v1/admin/features/agent-assist` | — | `{ enabled, provider, lastUpdatedUtc, lastUpdatedBy }` — credentials never surfaced |
| `PUT`  | `/api/v1/admin/features/agent-assist` | `{ enabled, provider, apiKey?, voiceId?, region?, endpoint? }` | 204 |

The `PUT` body is validated against a provider whitelist with
normalization (trim + lowercase). Supported values:

- `deepgram`
- `whisper`
- `azure-whisper`
- `google`
- `elevenlabs`
- `azure-tts`

Unknown providers return `400 Bad Request` with a descriptive message.

## Credential storage

Credentials (`apiKey`, `voiceId`, `region`, `endpoint`) are wrapped via
`IDataProtectionProvider` before being persisted by the runtime feature
store. The `GET` endpoint never surfaces them. Rotation is a simple
`PUT` with the new values; the engine picks them up on the next
session start (no restart required).

## RBAC hot-reload for existing tenants

The permission `features:agent-assist:manage` is added to
`RoleTemplateSeeder.AllPermissions()` and lands automatically on
fresh tenant seeds. **Existing tenants created before Platform v1.10.0
do NOT receive the new permission automatically.** To grant it, either:

1. Re-run the role seeder against the tenant (recommended):
   ```sh
   dotnet run --project tools/RoleReseed -- --tenant-id <id>
   ```
2. Or add the permission row manually to the `platform_admin` tenant role:
   ```sql
   INSERT INTO tenant_role_permissions (tenant_id, role_name, permission)
   VALUES ('<tenant-id>', 'platform_admin', 'features:agent-assist:manage');
   ```

Tracked in the **Platform v1.10 release runbook** as a required
migration step before any existing-tenant operator expects to use the
UI.

## DataProtection keyring persistence (Docker)

`AgentAssistCredentialsProtector` relies on ASP.NET's default
`IDataProtectionProvider`, which by default writes its keyring to
`/root/.aspnet/DataProtection-Keys` inside the container. That path is
**ephemeral** — a `docker compose down` + `up` cycle wipes it and every
stored credential becomes unrecoverable (they won't decrypt against a
new key).

Choose one of:

### Option A — Bind-mount the keyring directory (simple, 1-node)

```yaml
platform-api:
  # ...
  volumes:
    - ./data/dataprotection-keys:/root/.aspnet/DataProtection-Keys
```

### Option B — Persist keys via EF (multi-node, recommended)

In `Program.cs` add:

```csharp
builder.Services
    .AddDataProtection()
    .PersistKeysToDbContext<PlatformDbContext>()
    .SetApplicationName("asterisk-platform-api");
```

Any node in the cluster reads/writes the same key ring from the
Platform database. Pair this with `Asterisk.Platform.Identity.Redis`
(see `identity-redis.md`) so MFA + password-reset tokens also survive
node hops.

### Option C — Persist to Redis (if `Asterisk.Platform.Identity.Redis` is enabled)

```csharp
builder.Services
    .AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "dataprotection-keys")
    .SetApplicationName("asterisk-platform-api");
```

## Verification

1. `GET /api/v1/admin/features/agent-assist` returns `{ enabled: false, … }`
   on a fresh install.
2. `PUT` with a valid provider + apiKey; `GET` afterwards shows
   `enabled: true` but no apiKey value.
3. Start an AgentAssist-eligible call; the session goes through.
4. `PUT` with `enabled: false`; next call emits
   `agentassist.session.skipped{tenantId=...}` counter and no provider
   startup latency.

## Metrics to watch

All under meter `Asterisk.Sdk.Pro.AgentAssist`:

- `agentassist.session.started` — sessions that passed the toggle.
- `agentassist.session.skipped` — sessions rejected by the toggle.
- `agentassist.session.error` — provider or infrastructure failure.

## Known limitations

- **Sidebar "Features" single-item group** — the Web UI places the
  AgentAssist toggle under a "Features" sidebar group that currently
  has only this one entry. Accepted until a second runtime toggle lands
  (CallAnalytics / Retention candidates). Tracked as R5.2+ polish.
