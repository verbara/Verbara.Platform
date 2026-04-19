# Plan 30C: GDPR Compliance

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement GDPR data export, data purge with tombstone audit trail, and configurable retention policies per tenant.

**Architecture:** New GDPR services (export + purge) consume existing stores via new delete/list methods. PurgeLogStore provides tombstone persistence. RetentionPurgeService runs as background job. New GdprEndpoints expose admin/management API.

**Tech Stack:** .NET 10 Native AOT, Dapper, Npgsql.

**Spec:** `docs/superpowers/specs/2026-04-01-v130-integration-compliance-design.md` — Sub-project C.

**Prerequisite:** Plan 30B complete (oidc_subject column added to users table in migration).

---

## Phase A: Domain Models + Interface Extensions (Foundation)

### Step 1: Add PurgeEntry model to Platform.Core

- [ ] Create `src/Asterisk.Platform.Core/PurgeEntry.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// Tombstone record of a GDPR data purge — contains NO PII, only metadata.
/// </summary>
public sealed record PurgeEntry
{
    public required string PurgeId { get; init; }
    public required string TenantId { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string PerformedBy { get; init; }
    public required string Reason { get; init; }
    public required Dictionary<string, int> EntitiesDeleted { get; init; }
    public required DateTimeOffset PurgedAt { get; init; }
}
```

### Step 2: Add IPurgeLogStore to Platform.Core

- [ ] Create `src/Asterisk.Platform.Core/IPurgeLogStore.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// Persistence contract for GDPR purge tombstone records.
/// </summary>
public interface IPurgeLogStore
{
    Task SaveAsync(PurgeEntry entry, CancellationToken ct);
    Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken ct);
}
```

### Step 3: Add TenantRetentionPolicy model to Platform.Core

- [ ] Create `src/Asterisk.Platform.Core/TenantRetentionPolicy.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// Per-tenant data retention configuration. Null fields = indefinite retention (no auto-purge).
/// </summary>
public sealed record TenantRetentionPolicy
{
    public required string TenantId { get; init; }
    public int? ConversationRetentionDays { get; init; }
    public int? AuthEventRetentionDays { get; init; }
    public int? AuditRetentionDays { get; init; }
    public int? UsageRecordRetentionDays { get; init; }
}
```

### Step 4: Add ITenantRetentionPolicyStore to Platform.Core

- [ ] Create `src/Asterisk.Platform.Core/ITenantRetentionPolicyStore.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// Persistence contract for tenant data retention policies.
/// </summary>
public interface ITenantRetentionPolicyStore
{
    Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct);
    Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct);

    /// <summary>Returns tenants with at least one non-null retention field.</summary>
    Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct);
}
```

### Step 5: Extend IConversationStore with new methods

- [ ] Edit `src/Asterisk.Platform.Conversations/IConversationStore.cs`

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public interface IConversationStore
{
    Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct);
    Task SaveAsync(Conversation conversation, CancellationToken ct);
    Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct);

    /// <summary>Returns all conversations for a given contact (GDPR export).</summary>
    Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes all conversations for a contact and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct);

    /// <summary>Deletes conversations older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
```

### Step 6: Extend IMessageStore with new methods

- [ ] Edit `src/Asterisk.Platform.Conversations/Stores/IMessageStore.cs`

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Stores;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken ct);
    Task<Message?> GetByIdAsync(TenantId tenantId, EntityId messageId, CancellationToken ct);
    Task<IReadOnlyList<Message>> GetConversationMessagesAsync(TenantId tenantId, EntityId conversationId, int limit, int offset, CancellationToken ct);
    Task UpdateDeliveryStatusAsync(TenantId tenantId, EntityId messageId, MessageDeliveryStatus status, DateTimeOffset? timestamp, CancellationToken ct);
    Task<Message?> FindByExternalIdAsync(TenantId tenantId, string externalMessageId, CancellationToken ct);

    /// <summary>Returns all messages across multiple conversations (GDPR export).</summary>
    Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes all messages for the given conversations and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct);

    /// <summary>Deletes messages whose conversation no longer exists (retention cleanup).</summary>
    Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct);
}
```

### Step 7: Extend IAuthEventStore with new methods

- [ ] Edit `src/Asterisk.Platform.Identity/IAuthEventStore.cs`

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public interface IAuthEventStore
{
    Task SaveAsync(AuthEvent authEvent, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByTenantAsync(string tenantId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<AuthEvent>> ListByUserAsync(string tenantId, string userId, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<AuthEvent>> SearchAsync(string tenantId, AuthEventQuery query, CancellationToken ct);

    /// <summary>Returns all auth events for a user without pagination (GDPR export).</summary>
    Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct);

    /// <summary>Deletes all auth events for a user and returns the count deleted (GDPR purge).</summary>
    Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct);

    /// <summary>Deletes auth events older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
```

### Step 8: Extend IAuditStore with DeleteOlderThanAsync

- [ ] Edit `src/Asterisk.Platform.Audit/IAuditStore.cs`

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit;

/// <summary>
/// Persistence contract for audit entries. Implementations must be append-only
/// except for the retention-driven <see cref="DeleteOlderThanAsync"/> method.
/// </summary>
public interface IAuditStore
{
    /// <summary>Persists a new audit entry.</summary>
    Task SaveAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>
    /// Returns all audit entries for a specific entity, ordered by <see cref="AuditEntry.OccurredAt"/> ascending.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> GetByEntityAsync(TenantId tenantId, string entityType, string entityId, CancellationToken ct);

    /// <summary>Returns a paged set of audit entries matching the supplied query.</summary>
    Task<PagedResult<AuditEntry>> SearchAsync(TenantId tenantId, AuditQuery query, CancellationToken ct);

    /// <summary>Deletes audit entries older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
```

### Step 9: Extend IUsageRecordStore with DeleteOlderThanAsync

- [ ] Edit `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`

```csharp
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Billing;

/// <summary>
/// Persistence contract for usage records and aggregated summaries.
/// </summary>
public interface IUsageRecordStore
{
    /// <summary>Persists a single usage record.</summary>
    Task SaveAsync(UsageRecord record, CancellationToken ct);

    /// <summary>Persists a batch of usage records.</summary>
    Task SaveBatchAsync(IReadOnlyList<UsageRecord> records, CancellationToken ct);

    /// <summary>Returns aggregated summaries for a tenant within a date range, grouped by UsageType.</summary>
    Task<IReadOnlyList<UsageSummary>> GetSummaryAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Returns the aggregated summary for a specific usage type within a date range.</summary>
    Task<UsageSummary?> GetSummaryByTypeAsync(TenantId tenantId, UsageType type, DateTimeOffset from, DateTimeOffset until, CancellationToken ct);

    /// <summary>Returns paginated individual usage records for a tenant within a date range, optionally filtered by type.</summary>
    Task<IReadOnlyList<UsageRecord>> ListAsync(TenantId tenantId, DateTimeOffset from, DateTimeOffset until, UsageType? type, int page, int pageSize, CancellationToken ct);

    /// <summary>Deletes usage records older than cutoff and returns the count deleted (retention policy).</summary>
    Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct);
}
```

### Step 10: Add IGdprExportService and IGdprPurgeService to Platform.Core

- [ ] Create `src/Asterisk.Platform.Core/IGdprExportService.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// GDPR Article 20 — Right to Data Portability. Exports all PII for a contact.
/// </summary>
public interface IGdprExportService
{
    Task<GdprExportResult> ExportContactDataAsync(
        string tenantId, string contactId, CancellationToken ct);
}

/// <summary>
/// Result of a GDPR data export. Contains all PII associated with the subject.
/// </summary>
public sealed class GdprExportResult
{
    public required string ExportId { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public required GdprSubjectInfo Subject { get; init; }
    public object? Contact { get; init; }
    public IReadOnlyList<object>? Conversations { get; init; }
    public IReadOnlyList<object>? Messages { get; init; }
    public IReadOnlyList<object>? AuthEvents { get; init; }
    public IReadOnlyList<object>? AuditEntries { get; init; }
}

public sealed record GdprSubjectInfo(string ContactId, string TenantId);
```

- [ ] Create `src/Asterisk.Platform.Core/IGdprPurgeService.cs`

```csharp
namespace Asterisk.Platform.Core;

/// <summary>
/// GDPR Article 17 — Right to Erasure. Purges all PII for a contact with tombstone.
/// </summary>
public interface IGdprPurgeService
{
    Task<PurgeResult> PurgeContactDataAsync(
        string tenantId, string contactId, string performedBy,
        string reason, CancellationToken ct);
}

/// <summary>
/// Result of a GDPR purge operation. EntitiesDeleted maps entity type to count.
/// </summary>
public sealed record PurgeResult(
    string PurgeId,
    Dictionary<string, int> EntitiesDeleted,
    DateTimeOffset PurgedAt);
```

### Step 11: Build to verify interface changes compile

- [ ] Run `dotnet build Asterisk.Platform.slnx` — expect compilation errors from stores that do not yet implement new methods. Verify only expected errors (missing interface implementations in InMemory + Postgres stores). Do NOT fix yet — Phase B handles them.

---

## Phase B: InMemory Store Implementations (all new methods)

### Step 12: Implement new methods on InMemoryConversationStore

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/InMemoryConversationStore.cs` — add three new methods at end of class:

```csharp
    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        IReadOnlyList<Conversation> result = _items.Values
            .Where(c => c.TenantId == tenantId && c.ContactId == contactId)
            .OrderByDescending(c => c.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && kv.Value.ContactId == contactId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var toDelete = _items
            .Where(kv => kv.Key.Item1 == tenantId && kv.Value.CreatedAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }
```

### Step 13: Implement new methods on InMemoryMessageStore

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/InMemoryMessageStore.cs` — add three new methods at end of class:

```csharp
    public Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        var idSet = new HashSet<EntityId>(conversationIds);
        IReadOnlyList<Message> result = _items.Values
            .Where(m => m.TenantId == tenantId && idSet.Contains(m.ConversationId))
            .OrderBy(m => m.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        var idSet = new HashSet<EntityId>(conversationIds);
        var toDelete = _items
            .Where(kv => kv.Value.TenantId == tenantId && idSet.Contains(kv.Value.ConversationId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toDelete)
            _items.TryRemove(key, out _);

        return Task.FromResult(toDelete.Count);
    }

    public Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct)
    {
        // This requires knowing which conversations still exist — in-memory approximation:
        // We cannot query the conversation store from here, so orphaned = messages whose
        // conversation_id is not present in our own message set is not meaningful.
        // Instead, return 0 — orphan detection only works via SQL JOIN in Postgres.
        return Task.FromResult(0);
    }
```

### Step 14: Implement new methods on InMemoryAuthEventStore

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/InMemoryAuthEventStore.cs` — add three new methods at end of class:

```csharp
    public Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        IReadOnlyList<AuthEvent> result = _items
            .Where(e => e.TenantId == tenantId && e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        // ConcurrentBag does not support removal — rebuild
        var toKeep = _items.Where(e => !(e.TenantId == tenantId && e.UserId == userId)).ToList();
        var deleted = _items.Count - toKeep.Count;

        // Clear and re-add (ConcurrentBag has no RemoveWhere)
        while (_items.TryTake(out _)) { }
        foreach (var item in toKeep)
            _items.Add(item);

        return Task.FromResult(deleted);
    }

    public Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var toKeep = _items.Where(e => !(e.TenantId == tenantId && e.CreatedAt < cutoff)).ToList();
        var deleted = _items.Count - toKeep.Count;

        while (_items.TryTake(out _)) { }
        foreach (var item in toKeep)
            _items.Add(item);

        return Task.FromResult(deleted);
    }
```

### Step 15: Implement DeleteOlderThanAsync on InMemoryAuditStore

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/InMemoryAuditStore.cs` — add at end of class:

```csharp
    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!_entries.TryGetValue(tenantId, out var list))
            return Task.FromResult(0);

        int deleted;
        lock (list)
        {
            var before = list.Count;
            list.RemoveAll(e => e.OccurredAt < cutoff);
            deleted = before - list.Count;
        }

        return Task.FromResult(deleted);
    }
```

### Step 16: Implement DeleteOlderThanAsync on InMemoryUsageRecordStore

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs` — add at end of class:

```csharp
    public Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        if (!_records.TryGetValue(tenantId, out var list))
            return Task.FromResult(0);

        int deleted;
        lock (list)
        {
            var before = list.Count;
            list.RemoveAll(r => r.RecordedAt < cutoff);
            deleted = before - list.Count;
        }

        return Task.FromResult(deleted);
    }
```

### Step 17: Create InMemoryPurgeLogStore

- [ ] Create `src/Asterisk.Platform.Storage.InMemory/InMemoryPurgeLogStore.cs`

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryPurgeLogStore : IPurgeLogStore
{
    private readonly ConcurrentBag<PurgeEntry> _entries = [];

    public Task SaveAsync(PurgeEntry entry, CancellationToken ct)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken ct)
    {
        var filtered = _entries.AsEnumerable();

        if (!string.IsNullOrEmpty(tenantId))
            filtered = filtered.Where(e => e.TenantId == tenantId);
        if (from.HasValue)
            filtered = filtered.Where(e => e.PurgedAt >= from.Value);
        if (to.HasValue)
            filtered = filtered.Where(e => e.PurgedAt <= to.Value);

        var ordered = filtered.OrderByDescending(e => e.PurgedAt).ToList();
        var totalCount = ordered.Count;
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<PurgeEntry>(items, totalCount, page, pageSize));
    }
}
```

### Step 18: Create InMemoryTenantRetentionPolicyStore

- [ ] Create `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantRetentionPolicyStore.cs`

```csharp
using System.Collections.Concurrent;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantRetentionPolicyStore : ITenantRetentionPolicyStore
{
    private readonly ConcurrentDictionary<string, TenantRetentionPolicy> _policies = new();

    public Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct)
    {
        _policies.TryGetValue(tenantId, out var policy);
        return Task.FromResult(policy);
    }

    public Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct)
    {
        _policies[policy.TenantId] = policy;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantRetentionPolicy> result = _policies.Values
            .Where(p => p.ConversationRetentionDays.HasValue
                     || p.AuthEventRetentionDays.HasValue
                     || p.AuditRetentionDays.HasValue
                     || p.UsageRecordRetentionDays.HasValue)
            .ToList();
        return Task.FromResult(result);
    }
}
```

### Step 19: Register new stores in AddInMemoryStorage

- [ ] Edit `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs` — add after the existing Billing section:

```csharp
        // GDPR
        services.AddSingleton<IPurgeLogStore, InMemoryPurgeLogStore>();
        services.AddSingleton<ITenantRetentionPolicyStore, InMemoryTenantRetentionPolicyStore>();
```

### Step 20: Build to verify InMemory compiles

- [ ] Run `dotnet build Asterisk.Platform.slnx` — expect Postgres compilation errors only. InMemory should compile cleanly.

---

## Phase C: Postgres Store Implementations

### Step 21: Create migration 005_GdprCompliance.sql

- [ ] Create `src/Asterisk.Platform.Storage.Postgres/Migrations/005_GdprCompliance.sql`

```sql
-- 005_GdprCompliance.sql
-- GDPR: purge log + tenant retention policies
-- NOTE: oidc_subject column on users table is added in Plan 30B migration.

CREATE TABLE IF NOT EXISTS purge_log (
    purge_id        VARCHAR(36) PRIMARY KEY,
    tenant_id       VARCHAR(36) NOT NULL,
    subject_type    VARCHAR(50) NOT NULL,
    subject_id      VARCHAR(100) NOT NULL,
    performed_by    VARCHAR(100) NOT NULL,
    reason          VARCHAR(500) NOT NULL,
    entities_deleted JSONB NOT NULL,
    purged_at       TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_purge_log_tenant ON purge_log(tenant_id);
CREATE INDEX IF NOT EXISTS ix_purge_log_purged_at ON purge_log(purged_at DESC);

CREATE TABLE IF NOT EXISTS tenant_retention_policies (
    tenant_id                    VARCHAR(36) PRIMARY KEY,
    conversation_retention_days  INTEGER,
    auth_event_retention_days    INTEGER,
    audit_retention_days         INTEGER,
    usage_record_retention_days  INTEGER
);
```

### Step 22: Implement new methods on PostgresConversationStore

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresConversationStore.cs` — add three methods before the private helper section:

```csharp
    public async Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationRow>(
            "SELECT conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, case_id, " +
            "metadata, created_at, closed_at, updated_at, created_by, updated_by " +
            "FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId " +
            "ORDER BY created_at DESC",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
        return rows.Select(r => r.ToConversation()).ToList();
    }

    public async Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND contact_id = @ContactId",
            new { TenantId = tenantId.Value, ContactId = contactId.Value });
    }

    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM conversations WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }
```

### Step 23: Implement new methods on PostgresMessageStore

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresMessageStore.cs` — add three methods before the private helper section:

```csharp
    public async Task<IReadOnlyList<Message>> GetByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return [];

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = conversationIds.Select(id => id.Value).ToArray();
        var rows = await conn.QueryAsync<MessageRow>(
            "SELECT message_id, conversation_id, tenant_id, direction, channel, sender_id, content, " +
            "delivery_status, external_message_id, created_at, delivered_at, read_at, updated_at, created_by, updated_by " +
            "FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds) " +
            "ORDER BY created_at",
            new { TenantId = tenantId.Value, ConversationIds = ids });
        return rows.Select(r => r.ToMessage()).ToList();
    }

    public async Task<int> DeleteByConversationIdsAsync(TenantId tenantId, IReadOnlyList<EntityId> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = conversationIds.Select(id => id.Value).ToArray();
        return await conn.ExecuteAsync(
            "DELETE FROM messages WHERE tenant_id = @TenantId AND conversation_id = ANY(@ConversationIds)",
            new { TenantId = tenantId.Value, ConversationIds = ids });
    }

    public async Task<int> DeleteOrphanedAsync(TenantId tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM messages m WHERE m.tenant_id = @TenantId " +
            "AND NOT EXISTS (SELECT 1 FROM conversations c WHERE c.tenant_id = m.tenant_id AND c.conversation_id = m.conversation_id)",
            new { TenantId = tenantId.Value });
    }
```

### Step 24: Implement new methods on PostgresAuthEventStore

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuthEventStore.cs` — add three methods before the private helper section:

```csharp
    public async Task<IReadOnlyList<AuthEvent>> ListAllByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AuthEventRow>(
            "SELECT event_id, tenant_id, user_id, event_type, ip_address, user_agent, details, created_at " +
            "FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY created_at DESC",
            new { TenantId = tenantId, UserId = userId });
        return rows.Select(r => r.ToAuthEvent()).ToList();
    }

    public async Task<int> DeleteByUserAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND user_id = @UserId",
            new { TenantId = tenantId, UserId = userId });
    }

    public async Task<int> DeleteOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM auth_events WHERE tenant_id = @TenantId AND created_at < @Cutoff",
            new { TenantId = tenantId, Cutoff = cutoff });
    }
```

### Step 25: Implement DeleteOlderThanAsync on PostgresAuditStore

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs` — add before the private helper section:

```csharp
    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM audit_entries WHERE tenant_id = @TenantId AND occurred_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }
```

### Step 26: Implement DeleteOlderThanAsync on PostgresUsageRecordStore

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs` — add before the private helper section:

```csharp
    public async Task<int> DeleteOlderThanAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM usage_records WHERE tenant_id = @TenantId AND recorded_at < @Cutoff",
            new { TenantId = tenantId.Value, Cutoff = cutoff });
    }
```

### Step 27: Create PostgresPurgeLogStore

- [ ] Create `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPurgeLogStore.cs`

```csharp
using System.Text.Json;
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresPurgeLogStore : IPurgeLogStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPurgeLogStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task SaveAsync(PurgeEntry entry, CancellationToken ct)
    {
        var entitiesJson = JsonSerializer.Serialize(
            entry.EntitiesDeleted, PostgresJson.Ctx.DictionaryStringInt32);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO purge_log (purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at) " +
            "VALUES (@PurgeId, @TenantId, @SubjectType, @SubjectId, @PerformedBy, @Reason, @EntitiesDeleted::jsonb, @PurgedAt)",
            new
            {
                entry.PurgeId,
                entry.TenantId,
                entry.SubjectType,
                entry.SubjectId,
                entry.PerformedBy,
                entry.Reason,
                EntitiesDeleted = entitiesJson,
                entry.PurgedAt,
            });
    }

    public async Task<PagedResult<PurgeEntry>> ListAsync(
        string? tenantId, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken ct)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(tenantId))
        {
            conditions.Add("tenant_id = @TenantId");
            parameters.Add("TenantId", tenantId);
        }
        if (from.HasValue)
        {
            conditions.Add("purged_at >= @From");
            parameters.Add("From", from.Value);
        }
        if (to.HasValue)
        {
            conditions.Add("purged_at <= @To");
            parameters.Add("To", to.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var offset = (page - 1) * pageSize;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM purge_log {where}", parameters);

        parameters.Add("Limit", pageSize);
        parameters.Add("Offset", offset);

        var rows = await conn.QueryAsync<PurgeLogRow>(
            "SELECT purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at " +
            $"FROM purge_log {where} ORDER BY purged_at DESC LIMIT @Limit OFFSET @Offset",
            parameters);

        var items = rows.Select(r => r.ToPurgeEntry()).ToList();
        return new PagedResult<PurgeEntry>(items, total, page, pageSize);
    }

    private sealed record PurgeLogRow(
        string purge_id,
        string tenant_id,
        string subject_type,
        string subject_id,
        string performed_by,
        string reason,
        string entities_deleted,
        DateTimeOffset purged_at)
    {
        public PurgeEntry ToPurgeEntry() => new()
        {
            PurgeId = purge_id,
            TenantId = tenant_id,
            SubjectType = subject_type,
            SubjectId = subject_id,
            PerformedBy = performed_by,
            Reason = reason,
            EntitiesDeleted = JsonSerializer.Deserialize(
                entities_deleted, PostgresJson.Ctx.DictionaryStringInt32) ?? [],
            PurgedAt = purged_at,
        };
    }
}
```

### Step 28: Create PostgresTenantRetentionPolicyStore

- [ ] Create `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantRetentionPolicyStore.cs`

```csharp
using Dapper;
using Npgsql;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.Postgres.Stores;

internal sealed class PostgresTenantRetentionPolicyStore : ITenantRetentionPolicyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTenantRetentionPolicyStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RetentionRow>(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies WHERE tenant_id = @TenantId",
            new { TenantId = tenantId });
        return row?.ToPolicy();
    }

    public async Task SaveAsync(TenantRetentionPolicy policy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO tenant_retention_policies (tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days) " +
            "VALUES (@TenantId, @ConversationRetentionDays, @AuthEventRetentionDays, @AuditRetentionDays, @UsageRecordRetentionDays) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET " +
            "  conversation_retention_days = EXCLUDED.conversation_retention_days, " +
            "  auth_event_retention_days = EXCLUDED.auth_event_retention_days, " +
            "  audit_retention_days = EXCLUDED.audit_retention_days, " +
            "  usage_record_retention_days = EXCLUDED.usage_record_retention_days",
            new
            {
                policy.TenantId,
                policy.ConversationRetentionDays,
                policy.AuthEventRetentionDays,
                policy.AuditRetentionDays,
                policy.UsageRecordRetentionDays,
            });
    }

    public async Task<IReadOnlyList<TenantRetentionPolicy>> ListActiveAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RetentionRow>(
            "SELECT tenant_id, conversation_retention_days, auth_event_retention_days, " +
            "audit_retention_days, usage_record_retention_days " +
            "FROM tenant_retention_policies " +
            "WHERE conversation_retention_days IS NOT NULL " +
            "   OR auth_event_retention_days IS NOT NULL " +
            "   OR audit_retention_days IS NOT NULL " +
            "   OR usage_record_retention_days IS NOT NULL");
        return rows.Select(r => r.ToPolicy()).ToList();
    }

    private sealed record RetentionRow(
        string tenant_id,
        int? conversation_retention_days,
        int? auth_event_retention_days,
        int? audit_retention_days,
        int? usage_record_retention_days)
    {
        public TenantRetentionPolicy ToPolicy() => new()
        {
            TenantId = tenant_id,
            ConversationRetentionDays = conversation_retention_days,
            AuthEventRetentionDays = auth_event_retention_days,
            AuditRetentionDays = audit_retention_days,
            UsageRecordRetentionDays = usage_record_retention_days,
        };
    }
}
```

### Step 29: Register Dictionary<string, int> in PostgresJsonContext

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs` — add before `[JsonSourceGenerationOptions`:

```csharp
[JsonSerializable(typeof(Dictionary<string, int>))]
```

### Step 30: Register new Postgres stores in AddPostgresStorage

- [ ] Edit `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs` — add after the RBAC section:

```csharp
        // GDPR
        services.AddSingleton<IPurgeLogStore, PostgresPurgeLogStore>();
        services.AddSingleton<ITenantRetentionPolicyStore, PostgresTenantRetentionPolicyStore>();
```

Add the required using directive at top:

```csharp
using Asterisk.Platform.Core;
```

### Step 31: Build to verify all stores compile

- [ ] Run `dotnet build Asterisk.Platform.slnx` — should now compile cleanly. Fix any remaining issues.

---

## Phase D: GDPR Services

### Step 32: Create GdprExportService

- [ ] Create `src/Asterisk.Platform.Api/Services/GdprExportService.cs`

```csharp
using Asterisk.Platform.Audit;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class GdprExportService : IGdprExportService
{
    private readonly IContactStore _contactStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IAuditStore _auditStore;
    private readonly IUserStore _userStore;

    public GdprExportService(
        IContactStore contactStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IAuditStore auditStore,
        IUserStore userStore)
    {
        _contactStore = contactStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _auditStore = auditStore;
        _userStore = userStore;
    }

    public async Task<GdprExportResult> ExportContactDataAsync(
        string tenantId, string contactId, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var cid = EntityId.From(contactId);

        // 1. Contact profile
        var contact = await _contactStore.GetByIdAsync(tid, cid, ct);

        // 2. All conversations
        var conversations = await _conversationStore.ListByContactAsync(tid, cid, ct);

        // 3. All messages across conversations
        IReadOnlyList<Message> messages = [];
        if (conversations.Count > 0)
        {
            var conversationIds = conversations.Select(c => c.ConversationId).ToList();
            messages = await _messageStore.GetByConversationIdsAsync(tid, conversationIds, ct);
        }

        // 4. Auth events (if linked user exists — match by contact email)
        IReadOnlyList<AuthEvent>? authEvents = null;
        if (contact is not null)
        {
            // Try to find a user linked to this contact via email address
            var emailAddress = contact.Addresses.FirstOrDefault(a => a.Channel == ChannelType.Email);
            if (emailAddress is not null)
            {
                var user = await _userStore.FindByEmailAsync(tid, emailAddress.Address, ct);
                if (user is not null)
                    authEvents = await _authEventStore.ListAllByUserAsync(tenantId, user.UserId.Value, ct);
            }
        }

        // 5. Audit trail for the contact entity
        var auditEntries = await _auditStore.GetByEntityAsync(tid, "Contact", contactId, ct);

        return new GdprExportResult
        {
            ExportId = Guid.NewGuid().ToString("N"),
            ExportedAt = DateTimeOffset.UtcNow,
            Subject = new GdprSubjectInfo(contactId, tenantId),
            Contact = contact,
            Conversations = conversations.Cast<object>().ToList(),
            Messages = messages.Cast<object>().ToList(),
            AuthEvents = authEvents?.Cast<object>().ToList(),
            AuditEntries = auditEntries.Cast<object>().ToList(),
        };
    }
}
```

### Step 33: Create GdprPurgeService

- [ ] Create `src/Asterisk.Platform.Api/Services/GdprPurgeService.cs`

```csharp
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Api.Services;

internal sealed class GdprPurgeService : IGdprPurgeService
{
    private readonly IContactStore _contactStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IUserStore _userStore;
    private readonly IPurgeLogStore _purgeLogStore;

    public GdprPurgeService(
        IContactStore contactStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IUserStore userStore,
        IPurgeLogStore purgeLogStore)
    {
        _contactStore = contactStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _userStore = userStore;
        _purgeLogStore = purgeLogStore;
    }

    public async Task<PurgeResult> PurgeContactDataAsync(
        string tenantId, string contactId, string performedBy,
        string reason, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);
        var cid = EntityId.From(contactId);
        var entitiesDeleted = new Dictionary<string, int>();

        // 1. Find all conversations for this contact (need IDs for message deletion)
        var conversations = await _conversationStore.ListByContactAsync(tid, cid, ct);
        var conversationIds = conversations.Select(c => c.ConversationId).ToList();

        // 2. Delete messages first (referential integrity — messages reference conversations)
        if (conversationIds.Count > 0)
        {
            var messagesDeleted = await _messageStore.DeleteByConversationIdsAsync(tid, conversationIds, ct);
            if (messagesDeleted > 0)
                entitiesDeleted["messages"] = messagesDeleted;
        }

        // 3. Delete conversations
        var conversationsDeleted = await _conversationStore.DeleteByContactAsync(tid, cid, ct);
        if (conversationsDeleted > 0)
            entitiesDeleted["conversations"] = conversationsDeleted;

        // 4. Delete auth events for linked user (if any)
        var contact = await _contactStore.GetByIdAsync(tid, cid, ct);
        if (contact is not null)
        {
            var emailAddress = contact.Addresses.FirstOrDefault(a => a.Channel == ChannelType.Email);
            if (emailAddress is not null)
            {
                var user = await _userStore.FindByEmailAsync(tid, emailAddress.Address, ct);
                if (user is not null)
                {
                    var authEventsDeleted = await _authEventStore.DeleteByUserAsync(tenantId, user.UserId.Value, ct);
                    if (authEventsDeleted > 0)
                        entitiesDeleted["authEvents"] = authEventsDeleted;
                }
            }
        }

        // 5. Delete contact itself
        await _contactStore.DeleteAsync(tid, cid, ct);
        entitiesDeleted["contact"] = 1;

        // 6. Write tombstone (NO PII — only metadata)
        var purgeId = Guid.NewGuid().ToString("N");
        var purgedAt = DateTimeOffset.UtcNow;
        var purgeEntry = new PurgeEntry
        {
            PurgeId = purgeId,
            TenantId = tenantId,
            SubjectType = "contact",
            SubjectId = contactId,
            PerformedBy = performedBy,
            Reason = reason,
            EntitiesDeleted = entitiesDeleted,
            PurgedAt = purgedAt,
        };
        await _purgeLogStore.SaveAsync(purgeEntry, ct);

        return new PurgeResult(purgeId, entitiesDeleted, purgedAt);
    }
}
```

### Step 34: Create RetentionPurgeService (IHostedService)

- [ ] Create `src/Asterisk.Platform.Api/Services/RetentionPurgeService.cs`

```csharp
using Asterisk.Platform.Audit;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Asterisk.Platform.Api.Services;

internal sealed partial class RetentionPurgeService : BackgroundService
{
    private readonly ITenantRetentionPolicyStore _policyStore;
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;
    private readonly IAuthEventStore _authEventStore;
    private readonly IAuditStore _auditStore;
    private readonly IUsageRecordStore _usageRecordStore;
    private readonly IPurgeLogStore _purgeLogStore;
    private readonly IClock _clock;
    private readonly ILogger<RetentionPurgeService> _logger;
    private readonly TimeSpan _interval;

    public RetentionPurgeService(
        ITenantRetentionPolicyStore policyStore,
        IConversationStore conversationStore,
        IMessageStore messageStore,
        IAuthEventStore authEventStore,
        IAuditStore auditStore,
        IUsageRecordStore usageRecordStore,
        IPurgeLogStore purgeLogStore,
        IClock clock,
        ILogger<RetentionPurgeService> logger,
        IConfiguration configuration)
    {
        _policyStore = policyStore;
        _conversationStore = conversationStore;
        _messageStore = messageStore;
        _authEventStore = authEventStore;
        _auditStore = auditStore;
        _usageRecordStore = usageRecordStore;
        _purgeLogStore = purgeLogStore;
        _clock = clock;
        _logger = logger;

        var hours = configuration.GetValue("Retention:PurgeIntervalHours", 24);
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial run by 5 minutes to let the app start up
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRetentionPurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogRetentionError(ex);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    internal async Task RunRetentionPurgeAsync(CancellationToken ct)
    {
        var policies = await _policyStore.ListActiveAsync(ct);
        if (policies.Count == 0)
        {
            LogNoActivePolicies();
            return;
        }

        var now = _clock.UtcNow;

        foreach (var policy in policies)
        {
            var entitiesDeleted = new Dictionary<string, int>();
            var tid = new TenantId(policy.TenantId);

            // Conversations + orphaned messages
            if (policy.ConversationRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.ConversationRetentionDays.Value);
                var convDeleted = await _conversationStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (convDeleted > 0)
                    entitiesDeleted["conversations"] = convDeleted;

                var orphanedMsgs = await _messageStore.DeleteOrphanedAsync(tid, ct);
                if (orphanedMsgs > 0)
                    entitiesDeleted["orphanedMessages"] = orphanedMsgs;
            }

            // Auth events
            if (policy.AuthEventRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.AuthEventRetentionDays.Value);
                var deleted = await _authEventStore.DeleteOlderThanAsync(policy.TenantId, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["authEvents"] = deleted;
            }

            // Audit entries
            if (policy.AuditRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.AuditRetentionDays.Value);
                var deleted = await _auditStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["auditEntries"] = deleted;
            }

            // Usage records
            if (policy.UsageRecordRetentionDays.HasValue)
            {
                var cutoff = now.AddDays(-policy.UsageRecordRetentionDays.Value);
                var deleted = await _usageRecordStore.DeleteOlderThanAsync(tid, cutoff, ct);
                if (deleted > 0)
                    entitiesDeleted["usageRecords"] = deleted;
            }

            // Write tombstone if anything was deleted
            if (entitiesDeleted.Count > 0)
            {
                await _purgeLogStore.SaveAsync(new PurgeEntry
                {
                    PurgeId = Guid.NewGuid().ToString("N"),
                    TenantId = policy.TenantId,
                    SubjectType = "retention_policy",
                    SubjectId = policy.TenantId,
                    PerformedBy = "system",
                    Reason = "retention_policy",
                    EntitiesDeleted = entitiesDeleted,
                    PurgedAt = now,
                }, ct);

                LogRetentionPurgeCompleted(policy.TenantId, entitiesDeleted.Values.Sum());
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention purge completed for tenant {TenantId}: {TotalDeleted} entities deleted")]
    private partial void LogRetentionPurgeCompleted(string tenantId, int totalDeleted);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No active retention policies found — skipping purge cycle")]
    private partial void LogNoActivePolicies();

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention purge cycle failed")]
    private partial void LogRetentionError(Exception ex);
}
```

### Step 35: Verify IUserStore has FindByEmailAsync

- [ ] Check `src/Asterisk.Platform.Identity/IUserStore.cs` for `FindByEmailAsync` method. If it does not exist, add it:

```csharp
    Task<User?> FindByEmailAsync(TenantId tenantId, string email, CancellationToken ct);
```

And implement in `InMemoryUserStore` and `PostgresUserStore`. (Likely already exists from auth implementation.)

### Step 36: Build to verify services compile

- [ ] Run `dotnet build Asterisk.Platform.slnx` — resolve any compilation errors.

---

## Phase E: Endpoints + Wiring

### Step 37: Create GdprEndpoints

- [ ] Create `src/Asterisk.Platform.Api/Endpoints/GdprEndpoints.cs`

```csharp
using Asterisk.Platform.Api.Endpoints.Shared;
using Asterisk.Platform.Core;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class GdprEndpoints
{
    public static void MapGdprEndpoints(this IEndpointRouteBuilder app)
    {
        // Tenant admin endpoints
        var admin = app.MapGroup("/api/admin/gdpr").RequireAuthorization("AdminOnly");
        admin.MapPost("/export", ExportContactData);
        admin.MapPost("/purge", PurgeContactData);

        // Platform admin endpoints
        var mgmt = app.MapGroup("/api/management/gdpr").RequireAuthorization("PlatformAdminOnly");
        mgmt.MapGet("/purge-log", ListPurgeLog);

        // Retention policy endpoints (under existing management tenants path)
        var retention = app.MapGroup("/api/management/tenants/{tenantId}").RequireAuthorization("PlatformAdminOnly");
        retention.MapGet("/retention", GetRetentionPolicy);
        retention.MapPut("/retention", UpdateRetentionPolicy);
    }

    // ─── Export ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> ExportContactData(
        HttpContext context,
        [FromBody] GdprExportRequest body,
        [FromServices] IGdprExportService exportService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ContactId))
            return Results.BadRequest(new ErrorResponse("contactId is required"));

        var tenantId = GetTenantId(context);
        var result = await exportService.ExportContactDataAsync(tenantId.Value, body.ContactId, ct);
        return Results.Ok(result);
    }

    // ─── Purge ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> PurgeContactData(
        HttpContext context,
        [FromBody] GdprPurgeRequest body,
        [FromServices] IGdprPurgeService purgeService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ContactId))
            return Results.BadRequest(new ErrorResponse("contactId is required"));
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new ErrorResponse("reason is required"));

        var tenantId = GetTenantId(context);
        var userId = context.User.FindFirst("sub")?.Value ?? "unknown";

        var result = await purgeService.PurgeContactDataAsync(
            tenantId.Value, body.ContactId, userId, body.Reason, ct);

        return Results.Ok(result);
    }

    // ─── Purge Log ──────────────────────────────────────────────────────────────

    private static async Task<IResult> ListPurgeLog(
        [FromQuery] string? tenantId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] IPurgeLogStore store,
        CancellationToken ct)
    {
        var result = await store.ListAsync(tenantId, from, to, page ?? 1, pageSize ?? 50, ct);
        return Results.Ok(result);
    }

    // ─── Retention Policy ───────────────────────────────────────────────────────

    private static async Task<IResult> GetRetentionPolicy(
        string tenantId,
        [FromServices] ITenantRetentionPolicyStore store,
        CancellationToken ct)
    {
        var policy = await store.GetAsync(tenantId, ct);
        if (policy is null)
            return Results.Ok(new RetentionPolicyDto(tenantId, null, null, null, null));

        return Results.Ok(new RetentionPolicyDto(
            policy.TenantId,
            policy.ConversationRetentionDays,
            policy.AuthEventRetentionDays,
            policy.AuditRetentionDays,
            policy.UsageRecordRetentionDays));
    }

    private static async Task<IResult> UpdateRetentionPolicy(
        string tenantId,
        [FromBody] UpdateRetentionPolicyRequest body,
        [FromServices] ITenantRetentionPolicyStore store,
        CancellationToken ct)
    {
        var policy = new TenantRetentionPolicy
        {
            TenantId = tenantId,
            ConversationRetentionDays = body.ConversationRetentionDays,
            AuthEventRetentionDays = body.AuthEventRetentionDays,
            AuditRetentionDays = body.AuditRetentionDays,
            UsageRecordRetentionDays = body.UsageRecordRetentionDays,
        };

        await store.SaveAsync(policy, ct);
        return Results.Ok(new RetentionPolicyDto(
            tenantId,
            policy.ConversationRetentionDays,
            policy.AuthEventRetentionDays,
            policy.AuditRetentionDays,
            policy.UsageRecordRetentionDays));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;
        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── DTOs ───────────────────────────────────────────────────────────────────

internal sealed record GdprExportRequest(string ContactId);
internal sealed record GdprPurgeRequest(string ContactId, string Reason);

internal sealed record RetentionPolicyDto(
    string TenantId,
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);

internal sealed record UpdateRetentionPolicyRequest(
    int? ConversationRetentionDays,
    int? AuthEventRetentionDays,
    int? AuditRetentionDays,
    int? UsageRecordRetentionDays);
```

### Step 38: Register DTOs in ApiJsonContext

- [ ] Edit `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs` — add before `[JsonSourceGenerationOptions`:

```csharp
// GDPR
[JsonSerializable(typeof(GdprExportRequest))]
[JsonSerializable(typeof(GdprPurgeRequest))]
[JsonSerializable(typeof(GdprExportResult))]
[JsonSerializable(typeof(GdprSubjectInfo))]
[JsonSerializable(typeof(PurgeResult))]
[JsonSerializable(typeof(PagedResult<PurgeEntry>))]
[JsonSerializable(typeof(PurgeEntry))]
[JsonSerializable(typeof(RetentionPolicyDto))]
[JsonSerializable(typeof(UpdateRetentionPolicyRequest))]
[JsonSerializable(typeof(TenantRetentionPolicy))]
[JsonSerializable(typeof(Dictionary<string, int>))]
```

Add required usings at top:

```csharp
using Asterisk.Platform.Core;
```

(If `using Asterisk.Platform.Core;` already exists, skip it.)

### Step 39: Register GDPR services in Program.cs

- [ ] Edit `src/Asterisk.Platform.Api/Program.cs` — add after `builder.Services.AddPlatformBilling();`:

```csharp
// ─── GDPR Services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IGdprExportService, GdprExportService>();
builder.Services.AddSingleton<IGdprPurgeService, GdprPurgeService>();
```

Add the using at top:

```csharp
using Asterisk.Platform.Api.Services;
```

(If it already exists from other services, skip.)

### Step 40: Register RetentionPurgeService in Program.cs

- [ ] Edit `src/Asterisk.Platform.Api/Program.cs` — add after the GDPR services block:

```csharp
builder.Services.AddHostedService<RetentionPurgeService>();
```

### Step 41: Map GDPR endpoints in Program.cs

- [ ] Edit `src/Asterisk.Platform.Api/Program.cs` — add after `app.MapManagementImpersonationEndpoints();`:

```csharp
app.MapGdprEndpoints();
```

### Step 42: Build to verify endpoints and wiring compile

- [ ] Run `dotnet build Asterisk.Platform.slnx` — must compile with zero errors and zero warnings.

---

## Phase F: Tests

### Step 43: Create GDPR InMemory store tests

- [ ] Create `tests/Asterisk.Platform.Storage.InMemory.Tests/GdprStoreTests.cs`

```csharp
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class GdprStoreTests
{
    private const string Tenant1 = "tenant-1";
    private const string Tenant2 = "tenant-2";

    // ─── PurgeLogStore ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeLogStore_SaveAsync_ShouldPersistEntry()
    {
        var store = new InMemoryPurgeLogStore();
        var entry = MakePurgeEntry();

        await store.SaveAsync(entry, CancellationToken.None);

        var result = await store.ListAsync(Tenant1, null, null, 1, 10, CancellationToken.None);
        result.Items.Should().HaveCount(1);
        result.Items[0].PurgeId.Should().Be(entry.PurgeId);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldFilterByTenant()
    {
        var store = new InMemoryPurgeLogStore();
        await store.SaveAsync(MakePurgeEntry(Tenant1), CancellationToken.None);
        await store.SaveAsync(MakePurgeEntry(Tenant2), CancellationToken.None);

        var result = await store.ListAsync(Tenant1, null, null, 1, 10, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].TenantId.Should().Be(Tenant1);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldFilterByDateRange()
    {
        var store = new InMemoryPurgeLogStore();
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakePurgeEntry(purgedAt: base_), CancellationToken.None);
        await store.SaveAsync(MakePurgeEntry(purgedAt: base_.AddDays(10)), CancellationToken.None);

        var result = await store.ListAsync(null, base_.AddDays(5), null, 1, 10, CancellationToken.None);

        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task PurgeLogStore_ListAsync_ShouldSupportPaging()
    {
        var store = new InMemoryPurgeLogStore();
        for (var i = 0; i < 5; i++)
            await store.SaveAsync(MakePurgeEntry(), CancellationToken.None);

        var page1 = await store.ListAsync(null, null, null, 1, 2, CancellationToken.None);
        var page2 = await store.ListAsync(null, null, null, 2, 2, CancellationToken.None);

        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page1.TotalCount.Should().Be(5);
    }

    // ─── RetentionPolicyStore ───────────────────────────────────────────────────

    [Fact]
    public async Task RetentionPolicyStore_GetAsync_ShouldReturnNull_WhenNotSet()
    {
        var store = new InMemoryTenantRetentionPolicyStore();

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RetentionPolicyStore_SaveAsync_ShouldPersistAndRetrieve()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        var policy = new TenantRetentionPolicy
        {
            TenantId = Tenant1,
            ConversationRetentionDays = 90,
            AuthEventRetentionDays = 365,
        };

        await store.SaveAsync(policy, CancellationToken.None);
        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ConversationRetentionDays.Should().Be(90);
        result.AuthEventRetentionDays.Should().Be(365);
        result.AuditRetentionDays.Should().BeNull();
    }

    [Fact]
    public async Task RetentionPolicyStore_SaveAsync_ShouldOverwriteExisting()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 30 }, CancellationToken.None);
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 60 }, CancellationToken.None);

        var result = await store.GetAsync(Tenant1, CancellationToken.None);

        result!.ConversationRetentionDays.Should().Be(60);
    }

    [Fact]
    public async Task RetentionPolicyStore_ListActiveAsync_ShouldReturnOnlyNonNullPolicies()
    {
        var store = new InMemoryTenantRetentionPolicyStore();
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant1, ConversationRetentionDays = 30 }, CancellationToken.None);
        await store.SaveAsync(new TenantRetentionPolicy { TenantId = Tenant2 }, CancellationToken.None); // all null

        var result = await store.ListActiveAsync(CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(Tenant1);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static PurgeEntry MakePurgeEntry(string? tenantId = null, DateTimeOffset? purgedAt = null) => new()
    {
        PurgeId = Guid.NewGuid().ToString("N"),
        TenantId = tenantId ?? Tenant1,
        SubjectType = "contact",
        SubjectId = "contact-1",
        PerformedBy = "admin-user",
        Reason = "GDPR erasure request",
        EntitiesDeleted = new Dictionary<string, int> { ["messages"] = 10, ["conversations"] = 2, ["contact"] = 1 },
        PurgedAt = purgedAt ?? DateTimeOffset.UtcNow,
    };
}
```

### Step 44: Create GDPR endpoint tests

- [ ] Create `tests/Asterisk.Platform.Api.Tests/GdprEndpointTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

public sealed class GdprEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _adminClient;
    private readonly PlatformAdminApiFactory _factory;

    public GdprEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _adminClient = factory.CreatePlatformAdminClient();
    }

    // ─── Export ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ShouldReturnOk_WhenContactExists()
    {
        // Seed a contact
        var contactStore = _factory.Services.GetRequiredService<IContactStore>();
        var tenantId = new TenantId(PlatformAdminApiFactory.HostTenantId);
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = "GDPR",
            LastName = "Test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await contactStore.SaveAsync(contact, CancellationToken.None);

        var response = await _adminClient.PostAsJsonAsync(
            $"/api/admin/gdpr/export",
            new { contactId = contact.ContactId.Value });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("exportId");
        body.Should().Contain(contact.ContactId.Value);
    }

    [Fact]
    public async Task Export_ShouldReturnOk_WhenContactHasNoData()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/export",
            new { contactId = "nonexistent-contact" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("exportId");
    }

    [Fact]
    public async Task Export_ShouldReturnBadRequest_WhenContactIdMissing()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/export",
            new { contactId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Purge ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_ShouldReturnOk_AndCreateTombstone()
    {
        // Seed a contact
        var contactStore = _factory.Services.GetRequiredService<IContactStore>();
        var tenantId = new TenantId(PlatformAdminApiFactory.HostTenantId);
        var contact = new Contact
        {
            ContactId = EntityId.New(),
            TenantId = tenantId,
            FirstName = "Purge",
            LastName = "Subject",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await contactStore.SaveAsync(contact, CancellationToken.None);

        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/purge",
            new { contactId = contact.ContactId.Value, reason = "Subject erasure request" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("purgeId");
        body.Should().Contain("contact");
    }

    [Fact]
    public async Task Purge_ShouldReturnBadRequest_WhenReasonMissing()
    {
        var response = await _adminClient.PostAsJsonAsync(
            "/api/admin/gdpr/purge",
            new { contactId = "some-contact", reason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Purge Log ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeLog_ShouldReturnOk()
    {
        var response = await _adminClient.GetAsync("/api/management/gdpr/purge-log");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Retention Policy ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetentionPolicy_ShouldReturnDefaults_WhenNotConfigured()
    {
        var response = await _adminClient.GetAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(PlatformAdminApiFactory.HostTenantId);
    }

    [Fact]
    public async Task UpdateRetentionPolicy_ShouldReturnOk()
    {
        var response = await _adminClient.PutAsJsonAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention",
            new
            {
                conversationRetentionDays = 90,
                authEventRetentionDays = 365,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("90");
        body.Should().Contain("365");
    }

    [Fact]
    public async Task UpdateRetentionPolicy_ShouldPersistAndRetrieve()
    {
        await _adminClient.PutAsJsonAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention",
            new { conversationRetentionDays = 60, auditRetentionDays = 180 });

        var getResponse = await _adminClient.GetAsync(
            $"/api/management/tenants/{PlatformAdminApiFactory.HostTenantId}/retention");
        var body = await getResponse.Content.ReadAsStringAsync();

        body.Should().Contain("60");
        body.Should().Contain("180");
    }
}
```

### Step 45: Create GDPR store extension tests for existing stores

- [ ] Create `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryConversationStoreGdprTests.cs`

```csharp
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Asterisk.Platform.Storage.InMemory;
using FluentAssertions;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class InMemoryConversationStoreGdprTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public async Task ListByContactAsync_ShouldReturnConversationsForContact()
    {
        var store = new InMemoryConversationStore();
        var contactId = EntityId.New();
        var conv1 = MakeConversation(contactId: contactId);
        var conv2 = MakeConversation(contactId: contactId);
        var conv3 = MakeConversation(contactId: EntityId.New());
        await store.SaveAsync(conv1, CancellationToken.None);
        await store.SaveAsync(conv2, CancellationToken.None);
        await store.SaveAsync(conv3, CancellationToken.None);

        var result = await store.ListByContactAsync(Tenant1, contactId, CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteByContactAsync_ShouldDeleteAndReturnCount()
    {
        var store = new InMemoryConversationStore();
        var contactId = EntityId.New();
        await store.SaveAsync(MakeConversation(contactId: contactId), CancellationToken.None);
        await store.SaveAsync(MakeConversation(contactId: contactId), CancellationToken.None);
        await store.SaveAsync(MakeConversation(contactId: EntityId.New()), CancellationToken.None);

        var deleted = await store.DeleteByContactAsync(Tenant1, contactId, CancellationToken.None);

        deleted.Should().Be(2);
        var remaining = await store.ListByContactAsync(Tenant1, contactId, CancellationToken.None);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteOlderThanAsync_ShouldDeleteOldConversations()
    {
        var store = new InMemoryConversationStore();
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakeConversation(createdAt: cutoff.AddDays(-10)), CancellationToken.None);
        await store.SaveAsync(MakeConversation(createdAt: cutoff.AddDays(10)), CancellationToken.None);

        var deleted = await store.DeleteOlderThanAsync(Tenant1, cutoff, CancellationToken.None);

        deleted.Should().Be(1);
    }

    private static Conversation MakeConversation(
        EntityId? contactId = null,
        DateTimeOffset? createdAt = null) => new()
    {
        ConversationId = EntityId.New(),
        TenantId = Tenant1,
        ContactId = contactId ?? EntityId.New(),
        Channel = ChannelType.WebChat,
        State = ConversationState.Open,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };
}
```

### Step 46: Run all tests

- [ ] Run `dotnet test Asterisk.Platform.slnx` — all tests must pass, zero failures.

### Step 47: Build and verify zero warnings

- [ ] Run `dotnet build Asterisk.Platform.slnx` — zero warnings, zero errors.

---

## Phase G: Commit

### Step 48: Commit all changes

- [ ] Run:

```bash
git add -A
git commit -m "feat(api): add GDPR compliance — data export, purge with tombstone, retention policies

- Add IGdprExportService for Article 20 data portability (contact + conversations + messages + auth events)
- Add IGdprPurgeService for Article 17 right to erasure with tombstone (PurgeEntry in purge_log)
- Add TenantRetentionPolicy with configurable days per entity type (conversations, auth events, audit, usage)
- Add RetentionPurgeService background job (runs every 24h, writes tombstone per tenant)
- Extend IConversationStore with ListByContactAsync, DeleteByContactAsync, DeleteOlderThanAsync
- Extend IMessageStore with GetByConversationIdsAsync, DeleteByConversationIdsAsync, DeleteOrphanedAsync
- Extend IAuthEventStore with ListAllByUserAsync, DeleteByUserAsync, DeleteOlderThanAsync
- Extend IAuditStore and IUsageRecordStore with DeleteOlderThanAsync
- Add GdprEndpoints (44th endpoint group): export, purge, purge-log, retention CRUD
- Add InMemory + Postgres implementations for all new store methods
- Add migration 005_GdprCompliance.sql (purge_log + tenant_retention_policies tables)
- Add ~20 tests for GDPR stores and endpoints"
```

### Step 49: Update this plan file

- [ ] Mark all steps as complete: `- [x]`

---

## Summary

| Metric | Value |
|--------|-------|
| New files | ~12 (models, interfaces, services, stores, endpoints, migration, tests) |
| Modified files | ~14 (5 interfaces, 6 InMemory stores, 4 Postgres stores, Program.cs, ApiJsonContext, DI extensions) |
| New endpoints | 5 (export, purge, purge-log, retention GET, retention PUT) |
| Endpoint groups | 44 (was 43) |
| New tests | ~20 |
| Migration files | 1 (005_GdprCompliance.sql) |

### Files touched (alphabetical):

**New:**
- `src/Asterisk.Platform.Api/Endpoints/GdprEndpoints.cs`
- `src/Asterisk.Platform.Api/Services/GdprExportService.cs`
- `src/Asterisk.Platform.Api/Services/GdprPurgeService.cs`
- `src/Asterisk.Platform.Api/Services/RetentionPurgeService.cs`
- `src/Asterisk.Platform.Core/IGdprExportService.cs`
- `src/Asterisk.Platform.Core/IGdprPurgeService.cs`
- `src/Asterisk.Platform.Core/IPurgeLogStore.cs`
- `src/Asterisk.Platform.Core/ITenantRetentionPolicyStore.cs`
- `src/Asterisk.Platform.Core/PurgeEntry.cs`
- `src/Asterisk.Platform.Core/TenantRetentionPolicy.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryPurgeLogStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryTenantRetentionPolicyStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Migrations/005_GdprCompliance.sql`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresPurgeLogStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresTenantRetentionPolicyStore.cs`
- `tests/Asterisk.Platform.Api.Tests/GdprEndpointTests.cs`
- `tests/Asterisk.Platform.Storage.InMemory.Tests/GdprStoreTests.cs`
- `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryConversationStoreGdprTests.cs`

**Modified:**
- `src/Asterisk.Platform.Api/Program.cs`
- `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- `src/Asterisk.Platform.Audit/IAuditStore.cs`
- `src/Asterisk.Platform.Billing/IUsageRecordStore.cs`
- `src/Asterisk.Platform.Conversations/IConversationStore.cs`
- `src/Asterisk.Platform.Conversations/Stores/IMessageStore.cs`
- `src/Asterisk.Platform.Identity/IAuthEventStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryAuditStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryAuthEventStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryConversationStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryMessageStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/InMemoryUsageRecordStore.cs`
- `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- `src/Asterisk.Platform.Storage.Postgres/PostgresJsonSerializer.cs`
- `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuditStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresAuthEventStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresConversationStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresMessageStore.cs`
- `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresUsageRecordStore.cs`
