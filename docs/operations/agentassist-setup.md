# AgentAssist Runtime Setup

**Audience:** Platform operators enabling or rotating the AI provider
powering AgentAssist for a tenant. Shipped in Platform v1.10.0 + Pro
v1.12.0-pro (R5.1 Task J).

## What the runtime toggle does

`Verbara.Sdk.Pro.AgentAssist` exposes `IAgentAssistFeatureToggle`, a
consumer-supplied gate that the `AgentAssistEngine` consults at the
start of every call session. When the toggle returns `false`, the
engine:

1. Emits `agentassist.session.skipped` counter (`Verbara.Sdk.Pro.AgentAssist` meter).
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

## DataProtection keyring persistence

> **R5.2 (Platform 1.11.0): DB-backed by default.** Per [ADR-0003], `Program.cs`
> registers `PlatformDataProtectionDbContext` at startup and ASP.NET Core
> persists the keyring to the `data_protection_keys` table (migration `018_DataProtectionKeys.sql`).
> No operator action is required for the default flow — the same keyring is shared
> across all Platform.Api replicas reading the same database.
>
> **Backup procedure (R5.4 §S5.8) MUST include the `data_protection_keys` table.**
> Losing it makes every encrypted-at-rest credential unrecoverable.

### Default — DB-backed (multi-node, recommended)

Already wired in `Program.cs`:

```csharp
builder.Services.AddDbContext<PlatformDataProtectionDbContext>(opt =>
    opt.UseNpgsql(coreConnectionString));
builder.Services.AddPlatformDataProtection(opt =>
{
    opt.ApplicationName = "Verbara.Platform";
});
```

`AgentAssistCredentialsProtector` consumes the resulting `IDataProtectionProvider`
transparently. Container recycles preserve all encrypted credentials.

### Override — file-system mode (single-node deploys)

If a deploy doesn't have a Postgres connection (rare) or prefers a mounted volume:

```csharp
builder.Services.AddPlatformDataProtection(opt =>
{
    opt.ApplicationName = "Verbara.Platform";
    opt.UseFileSystem("/var/lib/verbara-platform/dataprotection-keys");
});
```

Pair with `docker-compose.yml`:

```yaml
platform-api:
  volumes:
    - ./data/dataprotection-keys:/var/lib/verbara-platform/dataprotection-keys
```

### Override — ephemeral mode (test/CI only)

```csharp
opt.UseEphemeralKeysForTesting();
```

Production callers MUST NOT use this — encrypted credentials are lost on every
process restart. The DI extension emits a startup warning when this mode is
selected so misconfigurations are loud in logs.

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

All under meter `Verbara.Sdk.Pro.AgentAssist`:

- `agentassist.session.started` — sessions that passed the toggle.
- `agentassist.session.skipped` — sessions rejected by the toggle.
- `agentassist.session.error` — provider or infrastructure failure.

## Known limitations

- **Sidebar "Features" single-item group** — the Web UI places the
  AgentAssist toggle under a "Features" sidebar group that currently
  has only this one entry. Accepted until a second runtime toggle lands
  (CallAnalytics / Retention candidates). Tracked as R5.2+ polish.
