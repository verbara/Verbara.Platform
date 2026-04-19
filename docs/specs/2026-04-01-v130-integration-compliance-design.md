# v1.3.0 "Integration & Compliance" Design Spec

**Goal:** Remove the four critical blockers preventing production CCaaS deployment: dormant licensing, incomplete OIDC SSO, zero GDPR compliance, and no outbound webhook integration.

**Execution order:** A → B → C → D (quick-wins first, largest subsystem last).

**Repos affected:** Asterisk.Platform (all 4), Asterisk.Sdk.Pro (sub-project A only — new ILicenseStatus interface).

---

## Sub-project A: License Enforcement

**Goal:** Activate the existing ECDSA P-256 licensing system in SDK Pro and add periodic runtime re-validation so expired licenses degrade gracefully instead of running forever unchecked.

**Current state:** SDK Pro has full licensing infrastructure (LicenseValidator, LicenseKey record, LicenseValidationHostedService, RequiredFeatureMarker, LicenseGenerator CLI). Platform.Api sets `EnforcementMode.Disabled` and passes `Array.Empty<byte>()` as the public key.

### Architecture

Two changes: (1) activate licensing in Program.cs with environment-aware defaults, (2) add a periodic re-validation hosted service that updates a queryable license status singleton.

**SDK Pro changes (Asterisk.Sdk.Pro.Licensing):** ILicenseStatus interface, LicenseStatusTracker implementation, LicenseRevalidationService hosted service, LicenseOptions.RevalidationInterval property. Requires pack + restore cycle.

**Platform changes:** Program.cs licensing config, management endpoint DTO enrichment. No new Platform files beyond endpoint changes.

### Components

#### A1: ILicenseStatus (new interface in SDK Pro Licensing)

```csharp
public interface ILicenseStatus
{
    bool IsValid { get; }
    LicenseValidationResult LastResult { get; }
    DateTimeOffset? ExpiresAt { get; }
    LicenseFeature LicensedFeatures { get; }
    DateTimeOffset LastValidatedAt { get; }
}
```

Implementation: `LicenseStatusTracker` (singleton, thread-safe). Updated by both LicenseValidationHostedService (startup) and LicenseRevalidationService (periodic).

#### A2: LicenseRevalidationService (new IHostedService in SDK Pro Licensing)

- Timer-based: re-validates every 6 hours (configurable via `LicenseOptions.RevalidationInterval`)
- Reads license file, validates signature + expiry via existing `LicenseValidator`
- Updates `ILicenseStatus` singleton
- If expired past grace period: logs `Critical`, sets `IsValid=false`
- Does NOT kill the process — allows graceful degradation

#### A3: Program.cs Changes (Platform.Api)

Replace:
```csharp
builder.Services.AddSingleton<byte[]>(Array.Empty<byte>());
builder.Services.AddProLicensing(o => o.EnforcementMode = EnforcementMode.Disabled);
```

With configuration-driven setup:
- `Licensing:FilePath` → license file path (default: `./license.lic`)
- `Licensing:EnforcementMode` → `Enforce` | `WarnOnly` | `Disabled`
- `Licensing:PublicKeyPath` → path to ECDSA public key (default: embedded resource)
- Development default: `WarnOnly` (no license file required)
- Production default: `Enforce`
- If no license file exists and mode is `Enforce`: startup fails with clear error message

#### A4: Management API Enrichment

`GET /api/management/system/license` — update existing endpoint response to include:
```json
{
  "isValid": true,
  "licensee": "Acme Corp",
  "licenseId": "lic-abc",
  "expiresAt": "2027-12-31T00:00:00Z",
  "gracePeriodEnds": "2028-01-07T00:00:00Z",
  "licensedFeatures": ["Cluster", "Dialer", "Analytics", "MultiTenant"],
  "maxNodes": 5,
  "lastValidatedAt": "2026-04-01T06:00:00Z",
  "enforcementMode": "Enforce"
}
```

### Not included (roadmap v1.3.1)

- Endpoint-level feature gates — middleware that blocks `/api/dialer/*` if `Dialer` feature is not licensed
- License activation API (online activation via license server)

### Tests

~8 tests: revalidation timer fires, license status transitions (valid→expired→grace→invalid), management API enrichment response, WarnOnly mode logs but continues, missing file in Enforce mode fails startup.

---

## Sub-project B: OIDC SSO Completion

**Goal:** Complete the OIDC callback that is currently a placeholder (`"OIDC token exchange not yet implemented"`). Implement Authorization Code + PKCE + nonce validation with automatic user provisioning.

**Current state:** OidcEndpoints has 3 routes mapped. Login builds authorization URL and redirects. Callback is a stub returning an error. Logout works. Per-tenant config exists: `OidcEnabled`, `OidcAuthority`, `OidcClientId`, `OidcClientSecret`, `OidcAutoCreateUsers`, `OidcDefaultRole`.

### Flow

```
1. GET /api/auth/oidc/login?tenant_id=X
   → Generate code_verifier (43-128 chars, RFC 7636) + code_challenge = SHA256(code_verifier)
   → Generate nonce (random 32 bytes, base64url)
   → Store {code_verifier, nonce, tenant_id, return_url} in encrypted cookie (5min TTL)
   → Redirect to IdP authorize endpoint:
     ?response_type=code
     &client_id={clientId}
     &redirect_uri={callbackUrl}
     &scope=openid profile email
     &code_challenge={challenge}
     &code_challenge_method=S256
     &nonce={nonce}
     &state={random}

2. GET /api/auth/oidc/callback?code=ABC&state=XYZ
   → Read + delete encrypted cookie → extract code_verifier, nonce, tenant_id
   → POST to IdP token endpoint: code + code_verifier + client_id + client_secret + redirect_uri
   → Receive: access_token, id_token, (optional refresh_token from IdP)
   → Validate id_token:
     - Fetch JWKS from {authority}/.well-known/openid-configuration (cached 24h)
     - Verify signature against JWKS
     - Verify issuer matches authority
     - Verify audience matches client_id
     - Verify expiry (exp claim)
     - Verify nonce claim matches cookie nonce
   → Extract claims: sub, email, name
   → User provisioning via OidcUserProvisioningService
   → Generate Platform JWT (access token 15min + refresh token 7d)
   → Set refresh_token cookie
   → Redirect to frontend (return_url or default)

3. POST /api/auth/oidc/logout (already works)
```

### Components

#### B1: OidcTokenExchangeService (new, Platform.Identity)

```csharp
public interface IOidcTokenExchangeService
{
    Task<OidcTokenResponse> ExchangeCodeAsync(
        string authority, string code, string codeVerifier,
        string redirectUri, string clientId, string clientSecret,
        CancellationToken ct);

    Task<OidcClaimsResult> ValidateIdTokenAsync(
        string idToken, string authority, string expectedAudience,
        string expectedNonce, CancellationToken ct);
}
```

- Uses `HttpClient` (injected via `IHttpClientFactory`) for token endpoint + JWKS fetch
- JWKS caching: in-memory, refreshed every 24h or on signature validation failure
- AOT-safe: `OidcJsonContext` with `[JsonSerializable]` for `OidcTokenResponse`, `OidcDiscoveryDocument`, `JsonWebKeySet`
- ID token validation: decode JWT header → find matching key in JWKS → verify RS256/ES256 signature → validate claims

**OidcTokenResponse:** `AccessToken`, `IdToken`, `TokenType`, `ExpiresIn`, `RefreshToken?`

**OidcClaimsResult:** `Subject` (sub), `Email`, `Name`, `EmailVerified`

#### B2: OidcUserProvisioningService (new, Platform.Identity)

```csharp
public interface IOidcUserProvisioningService
{
    Task<User> ProvisionOrUpdateAsync(
        string tenantId, OidcClaimsResult claims, CancellationToken ct);
}
```

Logic:
1. `IUserStore.FindByOidcSubjectAsync(tenantId, claims.Subject)` — lookup by IdP sub
2. If found: update name/email if changed, return user
3. If not found and `OidcAutoCreateUsers=true`:
   - Create user with email, name, `OidcSubject=claims.Subject`
   - Assign `OidcDefaultRole` (default: "Agent")
   - Log auth event: `oidc_login_success` with details `{ "action": "user_created" }`
4. If not found and `OidcAutoCreateUsers=false`:
   - Log auth event: `oidc_login_failure` with details `{ "reason": "user_not_found" }`
   - Return error

#### B3: IUserStore Extension

New method:
```csharp
Task<User?> FindByOidcSubjectAsync(string tenantId, string oidcSubject, CancellationToken ct);
```

User model extension:
- Add `OidcSubject: string?` property to User record

InMemory implementation: LINQ filter on OidcSubject.
Postgres implementation: `SELECT * FROM users WHERE tenant_id = @tid AND oidc_subject = @sub`
Migration: `ALTER TABLE users ADD COLUMN oidc_subject VARCHAR(255);` + index.

#### B4: PKCE + State Cookie

- Uses ASP.NET `IDataProtector` for cookie encryption (built-in, key-ring managed)
- Cookie name: `oidc_state`
- Cookie options: HttpOnly, Secure, SameSite=Lax, MaxAge=5min, Path=/api/auth/oidc
- Content: JSON `{ "code_verifier": "...", "nonce": "...", "tenant_id": "...", "return_url": "..." }`
- Deleted immediately after reading in callback

#### B5: New Auth Event Types

- `oidc_login_success` — user authenticated via OIDC
- `oidc_login_failure` — OIDC flow failed (token exchange error, nonce mismatch, user not found)

### Security

- PKCE S256 prevents authorization code interception
- Nonce validation prevents ID token replay attacks
- Encrypted cookie prevents CSRF on state parameter
- Client secret server-side only (never exposed to frontend)
- JWKS cached with forced refresh on unknown kid (key rotation support)
- 5-minute cookie TTL limits window for stale flow attacks

### Not included (roadmap)

- SAML 2.0 (v1.4.0)
- Multiple IdPs per tenant (v1.4.0) — current: 1 IdP per tenant
- IdP-initiated SSO (v1.4.0) — current: SP-initiated only
- OIDC group/role mapping from IdP claims (v1.4.0)

### Tests

~15 tests: token exchange with mocked IdP response, ID token validation (valid/expired/wrong-nonce/wrong-audience), PKCE verifier generation + challenge match, user provisioning create vs update, auto-create disabled rejection, expired cookie rejection, malformed callback parameters.

---

## Sub-project C: GDPR Compliance

**Goal:** Implement data export (Article 20), data purge with tombstone audit trail (Article 17), and configurable retention policies per tenant.

**Current state:** IContactStore has `DeleteAsync()`. IConversationStore and IMessageStore have NO delete methods. No export capability. No retention policies. Audit trail exists but doesn't cover purge operations.

### Architecture

Three capabilities: export, purge, retention — each with its own service, sharing store extensions and the purge_log tombstone table.

### C1: Data Export (Article 20 — Right to Data Portability)

**Endpoint:** `POST /api/admin/gdpr/export` (AdminOnly)

Request: `{ "contactId": "..." }`
Response: Streamed JSON file with all PII for the subject.

**IGdprExportService** (new interface, Platform.Core):
```csharp
Task<GdprExportResult> ExportContactDataAsync(
    string tenantId, string contactId, CancellationToken ct);
```

**GdprExportResult:**
```json
{
  "exportId": "exp-abc",
  "exportedAt": "2026-04-01T10:00:00Z",
  "subject": {
    "contactId": "contact-1",
    "tenantId": "tenant-1"
  },
  "contact": { ... },
  "conversations": [ ... ],
  "messages": [ ... ],
  "authEvents": [ ... ],
  "auditEntries": [ ... ]
}
```

Data sources:
- `IContactStore.GetByIdAsync` — contact profile
- `IConversationStore.ListByContactAsync` (new method) — all conversations
- `IMessageStore.GetByConversationIdsAsync` (new method) — messages across conversations
- `IAuthEventStore.ListByUserAsync` (new method) — auth events if linked user exists
- `IAuditStore.GetEntityHistoryAsync` — audit trail for the contact entity

#### New store methods for export:

**IConversationStore:**
```csharp
Task<IReadOnlyList<Conversation>> ListByContactAsync(
    string tenantId, string contactId, CancellationToken ct);
```

**IMessageStore:**
```csharp
Task<IReadOnlyList<Message>> GetByConversationIdsAsync(
    string tenantId, IReadOnlyList<string> conversationIds, CancellationToken ct);
```

**IAuthEventStore:**
```csharp
Task<IReadOnlyList<AuthEvent>> ListByUserAsync(
    string tenantId, string userId, CancellationToken ct);
```

### C2: Data Purge (Article 17 — Right to Erasure)

**Endpoint:** `POST /api/admin/gdpr/purge` (AdminOnly)

Request: `{ "contactId": "...", "reason": "Subject erasure request" }`
Response: `{ "purgeId": "...", "entitiesDeleted": { "messages": 42, "conversations": 5, "authEvents": 12, "contact": 1 } }`

**IGdprPurgeService** (new interface, Platform.Core):
```csharp
Task<PurgeResult> PurgeContactDataAsync(
    string tenantId, string contactId, string performedBy,
    string reason, CancellationToken ct);
```

**PurgeResult:** `PurgeId`, `EntitiesDeleted` (dictionary of entity type → count), `PurgedAt`.

**Deletion sequence** (order matters for referential integrity):
1. Messages of all conversations for the contact
2. Conversations of the contact
3. Auth events of the linked user (if exists)
4. Contact record itself

**Tombstone:** After successful purge, writes to `purge_log`:

**PurgeEntry:** `PurgeId`, `TenantId`, `SubjectType` ("contact"), `SubjectId`, `PerformedBy`, `Reason`, `EntitiesDeleted` (JSON), `PurgedAt`

The tombstone contains NO PII — only metadata about the purge operation itself.

#### New store methods for purge:

**IConversationStore:**
```csharp
Task<int> DeleteByContactAsync(string tenantId, string contactId, CancellationToken ct);
```

**IMessageStore:**
```csharp
Task<int> DeleteByConversationIdsAsync(
    string tenantId, IReadOnlyList<string> conversationIds, CancellationToken ct);
```

**IAuthEventStore:**
```csharp
Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct);
```

#### IPurgeLogStore (new interface):

```csharp
public interface IPurgeLogStore
{
    Task SaveAsync(PurgeEntry entry, CancellationToken ct);
    Task<PagedResult<PurgeEntry>> ListAsync(
        string tenantId, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken ct);
}
```

InMemory + Postgres implementations. Postgres table: `purge_log`.

### C3: Retention Policies

**Model:**
```csharp
public sealed record TenantRetentionPolicy(
    string TenantId,
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);
```

Null = indefinite (no auto-purge).

**ITenantRetentionPolicyStore** (new interface):
```csharp
Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct);
Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct);
Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct);
```

`ListActiveAsync` returns only tenants with at least one non-null retention field.

**RetentionPurgeService** (new IHostedService):
- Runs every 24 hours (configurable via `RetentionOptions.PurgeInterval`)
- For each tenant with active policy:
  - Delete conversations/messages older than `ConversationRetentionDays`
  - Delete auth events older than `AuthEventRetentionDays`
  - Delete audit entries older than `AuditRetentionDays`
  - Delete usage records older than `UsageRecordRetentionDays`
- Writes tombstone per tenant per run with `reason: "retention_policy"`
- Logs: count of purged records per tenant per entity type

#### New store methods for retention:

**IConversationStore:**
```csharp
Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
```

**IMessageStore:**
```csharp
Task<int> DeleteOrphanedAsync(string tenantId, CancellationToken ct);
```
Deletes messages whose conversation no longer exists (after conversation retention purge).

**IAuthEventStore:**
```csharp
Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
```

**IAuditStore:**
```csharp
Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
```

**IUsageRecordStore:**
```csharp
Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
```

#### Management API:

- `GET /api/management/tenants/{tenantId}/retention` — get retention policy
- `PUT /api/management/tenants/{tenantId}/retention` — update retention policy
- `GET /api/management/gdpr/purge-log` — list all purge tombstones (paginated, filterable by tenant/date)

### Postgres Migration

`004_GdprCompliance.sql`:
- `ALTER TABLE users ADD COLUMN oidc_subject VARCHAR(255);` (for Sub-project B)
- `CREATE INDEX ix_users_oidc_subject ON users(tenant_id, oidc_subject);`
- `CREATE TABLE purge_log (purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted JSONB, purged_at);`
- `CREATE TABLE tenant_retention_policies (tenant_id PRIMARY KEY, conversation_retention_days, auth_event_retention_days, audit_retention_days, usage_record_retention_days);`

### Not included (roadmap)

- PCI-DSS card data masking (v1.4.0)
- HIPAA BAA support (v1.4.0)
- CSV export format (v1.3.1 — JSON only for now)
- User-level purge (v1.3.1 — contact-level only for now)

### Tests

~20 tests: export with full data, export with no conversations, purge cascade order, tombstone created after purge, retention job fires and purges, retention with null policy = no-op, all new store delete/list methods (InMemory), purge-log pagination.

---

## Sub-project D: Outbound Webhooks

**Goal:** Enable tenants to subscribe to platform events and receive HTTP POST deliveries with persistent queue, exponential backoff retry, HMAC-SHA256 signing, and dead-letter queue.

**Current state:** Zero outbound webhook capability. PlatformEventBus is in-memory Rx.NET with 11 event types. Only inbound channel webhooks exist (WhatsApp/SMS/etc. → Platform).

### Architecture

```
PlatformEventBus.Events (Rx.NET Subject)
  → WebhookDispatcher (subscriber, singleton)
      → Filters: tenant + event type vs active subscriptions
      → Creates WebhookDelivery record (Status=Pending) in store
      → Enqueues in Channel<WebhookDelivery> (in-memory)

WebhookDeliveryService (IHostedService, background)
  → Reads from Channel (new events) + polls DB every 30s (pending retries)
  → For each delivery:
      → HTTP POST to endpoint URL
      → Headers: X-Webhook-Id, X-Webhook-Event, X-Webhook-Timestamp, X-Webhook-Signature
      → Signature: HMAC-SHA256(timestamp + "." + body, subscription.Secret)
      → 2xx → Status=Delivered
      → Timeout/4xx/5xx → Attempts++, schedule NextRetryAt with exponential backoff
      → Attempts >= MaxAttempts → Status=DeadLetter
```

### D1: Data Models

**WebhookSubscription:**
```csharp
public sealed record WebhookSubscription(
    string SubscriptionId,
    string TenantId,
    string Name,
    string EndpointUrl,
    string Secret,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

**WebhookDelivery:**
```csharp
public sealed record WebhookDelivery(
    string DeliveryId,
    string TenantId,
    string SubscriptionId,
    string EventType,
    string Payload,
    WebhookDeliveryStatus Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? NextRetryAt,
    int? LastResponseCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeliveredAt);

public enum WebhookDeliveryStatus { Pending, Delivered, Failed, DeadLetter }
```

**WebhookEventPayload:**
```json
{
  "id": "evt_abc123",
  "type": "conversation.message",
  "tenantId": "tenant-1",
  "timestamp": "2026-04-01T10:00:00Z",
  "data": { ... }
}
```

### D2: Event Type Registry

Dot-separated naming convention (like Stripe/GitHub):

| Platform Event | Webhook Event Type |
|---|---|
| ConversationAssignedEvent | `conversation.assigned` |
| ConversationMessageEvent | `conversation.message` |
| ConversationStateChangedEvent | `conversation.state_changed` |
| AgentStateChangedEvent | `agent.state_changed` |
| CampaignStatusChangedEvent | `campaign.status_changed` |
| CampaignMetricsUpdatedEvent | `campaign.metrics_updated` |
| CampaignDispositionSubmittedEvent | `campaign.disposition_submitted` |
| AgentAssistSuggestionEvent | `agent_assist.suggestion` |
| AgentAssistSentimentEvent | `agent_assist.sentiment` |
| AgentAssistComplianceAlertEvent | `agent_assist.compliance_alert` |
| AgentAssistTranscriptEvent | `agent_assist.transcript` |

11 event types at launch. New events added as PlatformEventBus grows.

### D3: Store Interfaces

**IWebhookSubscriptionStore:**
```csharp
public interface IWebhookSubscriptionStore
{
    Task<WebhookSubscription?> GetByIdAsync(string subscriptionId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> ListByTenantAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscription>> GetActiveByEventTypeAsync(
        string tenantId, string eventType, CancellationToken ct);
    Task SaveAsync(WebhookSubscription subscription, CancellationToken ct);
    Task DeleteAsync(string subscriptionId, CancellationToken ct);
}
```

**IWebhookDeliveryStore:**
```csharp
public interface IWebhookDeliveryStore
{
    Task SaveAsync(WebhookDelivery delivery, CancellationToken ct);
    Task<WebhookDelivery?> GetByIdAsync(string deliveryId, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ListPendingRetriesAsync(
        DateTimeOffset now, int batchSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListBySubscriptionAsync(
        string subscriptionId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<WebhookDelivery>> ListDeadLetterAsync(
        string tenantId, int page, int pageSize, CancellationToken ct);
    Task UpdateAsync(WebhookDelivery delivery, CancellationToken ct);
    Task DeleteBySubscriptionAsync(string subscriptionId, CancellationToken ct);
}
```

### D4: Services

**WebhookDispatcher** (singleton):
- Subscribes to `PlatformEventBus.Events` on startup
- Maps each `PlatformEvent` subtype to its webhook event type string
- For each event: queries `IWebhookSubscriptionStore.GetActiveByEventTypeAsync(tenantId, eventType)`
- For each matching subscription: creates `WebhookDelivery` (Pending), saves to store, enqueues in `Channel<WebhookDelivery>`
- Serializes event data using `WebhookJsonContext` (AOT-safe)

**WebhookDeliveryService** (IHostedService):
- Dual input loop:
  - `Channel<WebhookDelivery>` reader — new deliveries (immediate)
  - DB poll every 30 seconds — `ListPendingRetriesAsync(now, batchSize=100)` for retries
- HTTP delivery:
  - `HttpClient` with 10-second timeout
  - Headers: `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Timestamp`, `X-Webhook-Signature`
  - Signature computation: `HMAC-SHA256(timestamp + "." + body, secret)`
  - Content-Type: `application/json`
- Result handling:
  - 2xx: update Status=Delivered, DeliveredAt=now
  - Timeout/network error/4xx/5xx: Attempts++, compute NextRetryAt, update LastResponseCode/LastError
  - Attempts >= MaxAttempts (default 8): Status=DeadLetter

**Exponential backoff schedule (8 attempts, ~24h total):**

| Attempt | Delay | Cumulative |
|---------|-------|-----------|
| 1 | immediate | 0 |
| 2 | 1 min | 1 min |
| 3 | 5 min | 6 min |
| 4 | 30 min | 36 min |
| 5 | 2 hours | ~2.5h |
| 6 | 5 hours | ~7.5h |
| 7 | 8 hours | ~15.5h |
| 8 | 8 hours | ~23.5h |

Backoff formula: `delays = [0, 60, 300, 1800, 7200, 18000, 28800, 28800]` (seconds, hardcoded array — simple and predictable).

**WebhookSignatureService** (static helper):
```csharp
public static string ComputeSignature(string timestamp, string body, string secret)
    => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret),
        Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
```

### D5: Tenant API Endpoints

Note: Existing inbound webhooks live at `/api/webhooks/{tenantId}/{channel}`. Outbound subscription management uses `/api/webhooks/subscriptions` — no path conflict.

Under `/api/webhooks/subscriptions` (Authenticated, tenant-scoped):

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | List tenant's webhook subscriptions |
| POST | `/` | Create subscription (validates HTTPS URL, generates secret) |
| GET | `/{id}` | Get subscription with masked secret |
| PUT | `/{id}` | Update subscription (name, URL, event types, active) |
| DELETE | `/{id}` | Delete subscription + cancel pending deliveries |
| POST | `/{id}/test` | Send test event (`webhook.test`) to verify endpoint |
| GET | `/{id}/deliveries` | Delivery history (paginated) |
| POST | `/{id}/rotate-secret` | Generate new HMAC secret |

Under `/api/management/webhooks` (PlatformAdminOnly):

| Method | Path | Description |
|--------|------|-------------|
| GET | `/dead-letter` | List all dead-letter deliveries across tenants |
| POST | `/dead-letter/{id}/retry` | Re-enqueue a dead letter delivery (resets attempts, Status=Pending) |

Under `/api/webhooks/event-types` (Authenticated):

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | List available event types with descriptions |

### D6: Postgres Tables

`005_OutboundWebhooks.sql`:

```sql
CREATE TABLE webhook_subscriptions (
    subscription_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    endpoint_url VARCHAR(2000) NOT NULL,
    secret VARCHAR(64) NOT NULL,
    event_types JSONB NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_webhook_subscriptions_tenant ON webhook_subscriptions(tenant_id);

CREATE TABLE webhook_deliveries (
    delivery_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    subscription_id VARCHAR(36) NOT NULL REFERENCES webhook_subscriptions(subscription_id),
    event_type VARCHAR(100) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 8,
    next_retry_at TIMESTAMPTZ,
    last_response_code INTEGER,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ
);
CREATE INDEX ix_webhook_deliveries_pending ON webhook_deliveries(next_retry_at)
    WHERE status = 'Pending' AND next_retry_at IS NOT NULL;
CREATE INDEX ix_webhook_deliveries_subscription ON webhook_deliveries(subscription_id);
CREATE INDEX ix_webhook_deliveries_dead_letter ON webhook_deliveries(tenant_id)
    WHERE status = 'DeadLetter';
```

### D7: AOT Serialization

New `WebhookJsonContext` in Platform.Api serialization:
- `WebhookSubscription`, `List<WebhookSubscription>`
- `WebhookDelivery`, `List<WebhookDelivery>`, `PagedResult<WebhookDelivery>`
- `WebhookEventPayload`
- All request/response DTOs for subscription endpoints

### Not included (roadmap v1.4.0)

- Circuit breaker per-endpoint (auto-pause failing endpoints)
- Webhook logs UI in Platform.Web
- Batch delivery (multiple events per POST)
- Event replay (re-send all events from a time range)
- IP allowlisting for webhook origins

### Tests

~25 tests: subscription CRUD, HTTPS-only URL validation, dispatch event type filtering, delivery success (2xx), delivery failure + retry scheduling, exponential backoff timing, HMAC signature computation + verification, dead-letter transition after max attempts, dead-letter retry re-enqueue, test endpoint delivery, delete subscription cancels pending deliveries, rotate-secret generates new secret.

---

## Postgres Migration Summary

Three migration files:

**004_OidcSubject.sql** (Sub-project B):
- `ALTER TABLE users ADD COLUMN oidc_subject VARCHAR(255);`
- `CREATE INDEX ix_users_oidc_subject ON users(tenant_id, oidc_subject);`

**005_GdprCompliance.sql** (Sub-project C):
- `CREATE TABLE purge_log (...);`
- `CREATE TABLE tenant_retention_policies (...);`

**006_OutboundWebhooks.sql** (Sub-project D):
- `CREATE TABLE webhook_subscriptions (...);`
- `CREATE TABLE webhook_deliveries (...);`
- Indexes for pending retries, dead-letter queries

---

## DI Registration

```csharp
// Sub-project A: License Enforcement
// Modify existing AddProLicensing() call + add ILicenseStatus

// Sub-project B: OIDC
builder.Services.AddSingleton<IOidcTokenExchangeService, OidcTokenExchangeService>();
builder.Services.AddSingleton<IOidcUserProvisioningService, OidcUserProvisioningService>();
builder.Services.AddHttpClient("oidc"); // named HttpClient for IdP calls

// Sub-project C: GDPR
builder.Services.AddSingleton<IGdprExportService, GdprExportService>();
builder.Services.AddSingleton<IGdprPurgeService, GdprPurgeService>();
builder.Services.AddHostedService<RetentionPurgeService>();

// Sub-project D: Webhooks
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService<WebhookDeliveryService>();
builder.Services.AddHttpClient("webhooks"); // named HttpClient for deliveries
```

Storage registration follows existing pattern: interfaces in `AddInMemoryStorage()` and `AddPostgresStorage()`.

---

## Endpoint Summary

| Sub-project | New Endpoints | Modified Endpoints |
|---|---|---|
| A: License | 0 | 1 (enrich GET /api/management/system/license) |
| B: OIDC | 0 | 2 (rewrite login + callback) |
| C: GDPR | 5 | 0 |
| D: Webhooks | 13 | 0 |
| **Total** | **18 new** | **3 modified** |

New endpoint groups: GdprEndpoints (3 routes), WebhookSubscriptionEndpoints (8 routes), ManagementWebhookEndpoints (2 routes), WebhookEventTypeEndpoints (1 route). Total: 47 endpoint groups (was 43).

---

## Test Estimate

| Sub-project | New Tests |
|---|---|
| A: License | ~8 |
| B: OIDC | ~15 |
| C: GDPR | ~20 |
| D: Webhooks | ~25 |
| **Total** | **~68 new tests** |

Platform total after v1.3.0: ~1,230 tests (was 1,162).

---

## Roadmap Items Deferred

| Item | Target | Reason |
|---|---|---|
| Endpoint-level license feature gates | v1.3.1 | Startup + periodic check covers 95% of cases |
| Circuit breaker for webhook endpoints | v1.4.0 | DLQ already prevents infinite queuing |
| SAML 2.0 | v1.4.0 | OIDC covers majority of enterprise IdPs |
| Multiple IdPs per tenant | v1.4.0 | 1 IdP per tenant sufficient for launch |
| OIDC group/role mapping | v1.4.0 | Default role assignment sufficient for launch |
| CSV export format for GDPR | v1.3.1 | JSON covers the legal requirement |
| User-level purge (not just contact) | v1.3.1 | Contact-level covers primary GDPR use case |
| Webhook logs UI | v1.4.0 | API-first; UI follows |
| Webhook event replay | v1.4.0 | DLQ retry covers immediate need |
| Webhook batch delivery | v1.4.0 | Per-event delivery simpler and sufficient at launch |
