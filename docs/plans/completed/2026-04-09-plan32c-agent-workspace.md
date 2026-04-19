# Plan 32C: Agent Workspace Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add canned responses backend (model, store, CRUD + search API) and supervisor digital conversation monitoring (5 new endpoints for visibility into WebChat/WhatsApp/Email conversations).

**Architecture:** CannedResponse model + ICannedResponseStore in Platform.Conversations, InMemory store, Postgres store + migration 014, CannedResponseEndpoints (Admin CRUD + Agent search). SupervisorEndpoints extended with 5 digital monitoring endpoints using existing IConversationStore/IMessageStore/IConversationSwitchboard.

**Tech Stack:** .NET 10, Dapper (Postgres), xUnit + FluentAssertions + NSubstitute

---

### Task 1: Canned Response Model + Store Interface + InMemory Implementation

**Files:**
- Create: `src/Asterisk.Platform.Conversations/CannedResponse.cs`
- Create: `src/Asterisk.Platform.Conversations/ICannedResponseStore.cs`
- Create: `src/Asterisk.Platform.Storage.InMemory/InMemoryCannedResponseStore.cs`
- Modify: `src/Asterisk.Platform.Storage.InMemory/ServiceCollectionExtensions.cs`
- Test: `tests/Asterisk.Platform.Storage.InMemory.Tests/InMemoryCannedResponseStoreTests.cs`

- [ ] **Step 1: Create CannedResponse model**

```csharp
// src/Asterisk.Platform.Conversations/CannedResponse.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed class CannedResponse
{
    public required EntityId ResponseId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Shortcut { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? Category { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Create ICannedResponseStore interface**

```csharp
// src/Asterisk.Platform.Conversations/ICannedResponseStore.cs
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public interface ICannedResponseStore
{
    Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct);
    Task SaveAsync(CannedResponse response, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct);
}
```

- [ ] **Step 3: Create InMemoryCannedResponseStore**

```csharp
// src/Asterisk.Platform.Storage.InMemory/InMemoryCannedResponseStore.cs
using System.Collections.Concurrent;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Storage.InMemory;

public sealed class InMemoryCannedResponseStore : ICannedResponseStore
{
    private readonly ConcurrentDictionary<string, CannedResponse> _store = new();

    private static string Key(TenantId t, EntityId id) => $"{t.Value}:{id.Value}";

    public Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        _store.TryGetValue(Key(tenantId, responseId), out var result);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var items = _store.Values
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Shortcut)
            .ToList();
        return Task.FromResult<IReadOnlyList<CannedResponse>>(items);
    }

    public Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct)
    {
        var q = query.ToUpperInvariant();
        var items = _store.Values
            .Where(r => r.TenantId == tenantId &&
                (r.Shortcut.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 r.Body.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 (r.Category is not null && r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                 r.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(r => r.Shortcut)
            .ToList();
        return Task.FromResult<IReadOnlyList<CannedResponse>>(items);
    }

    public Task SaveAsync(CannedResponse response, CancellationToken ct)
    {
        _store[Key(response.TenantId, response.ResponseId)] = response;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        _store.TryRemove(Key(tenantId, responseId), out _);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Register in InMemory DI**

Add `services.AddSingleton<ICannedResponseStore, InMemoryCannedResponseStore>();` in `AddInMemoryStorage()`.

- [ ] **Step 5: Write InMemory store tests**

6 tests: Save+Get, ListByTenant, Search by shortcut, Search by category, Delete, Tenant isolation.

- [ ] **Step 6: Run tests, commit**

```bash
git commit -m "feat: add CannedResponse model, ICannedResponseStore, and InMemory implementation"
```

---

### Task 2: Postgres Canned Response Store + Migration 014

**Files:**
- Create: `src/Asterisk.Platform.Storage.Postgres/Stores/PostgresCannedResponseStore.cs`
- Create: `src/Asterisk.Platform.Storage.Postgres/Migrations/014_CannedResponses.sql`
- Modify: `src/Asterisk.Platform.Storage.Postgres/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Create migration 014**

```sql
-- 014_CannedResponses.sql
CREATE TABLE IF NOT EXISTS canned_responses (
    response_id TEXT NOT NULL,
    tenant_id   TEXT NOT NULL,
    shortcut    TEXT NOT NULL,
    title       TEXT NOT NULL,
    body        TEXT NOT NULL,
    category    TEXT,
    tags        TEXT,
    created_by  TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, response_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canned_responses_shortcut
    ON canned_responses (tenant_id, shortcut);
```

- [ ] **Step 2: Create PostgresCannedResponseStore**

Class-based row type with `{get; init;}`. Dapper queries with tenant_id filter. Search uses ILIKE. Tags stored as JSON text.

- [ ] **Step 3: Register in Postgres DI**

- [ ] **Step 4: Build, commit**

```bash
git commit -m "feat: add PostgresCannedResponseStore with migration 014"
```

---

### Task 3: Canned Response Endpoints

**Files:**
- Create: `src/Asterisk.Platform.Api/Endpoints/CannedResponseEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Modify: `src/Asterisk.Platform.Api/Program.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/CannedResponseEndpointTests.cs`

- [ ] **Step 1: Create CannedResponseEndpoints**

Admin CRUD (AdminOnly): GET list, POST create, PUT update, DELETE.
Agent search (Authenticated): GET /canned-responses?q={query}.

- [ ] **Step 2: Register DTOs in ApiJsonContext, map in Program.cs**

- [ ] **Step 3: Write endpoint tests**

6 tests: CRUD, search, duplicate shortcut rejected, tenant isolation.

- [ ] **Step 4: Run tests, commit**

```bash
git commit -m "feat: add CannedResponseEndpoints with admin CRUD and agent search"
```

---

### Task 4: Supervisor Digital Conversation Monitoring

**Files:**
- Modify: `src/Asterisk.Platform.Api/Endpoints/SupervisorEndpoints.cs`
- Modify: `src/Asterisk.Platform.Api/Serialization/ApiJsonContext.cs`
- Test: `tests/Asterisk.Platform.Api.Tests/SupervisorDigitalTests.cs`

5 new endpoints in SupervisorEndpoints:
1. `GET /supervisor/conversations` — list active digital conversations with filters
2. `GET /supervisor/conversations/{id}/messages` — view messages read-only
3. `POST /supervisor/conversations/{id}/takeover` — supervisor takes ownership
4. `POST /supervisor/conversations/{id}/close` — force close with reason
5. `POST /supervisor/conversations/{id}/note` — coaching note (agent_only visibility)

- [ ] **Step 1: Add 5 endpoints to SupervisorEndpoints**
- [ ] **Step 2: Register DTOs in ApiJsonContext**
- [ ] **Step 3: Write tests**

5 tests: list filtered, view messages, takeover, force close, coaching note.

- [ ] **Step 4: Run tests, commit**

```bash
git commit -m "feat: add supervisor digital conversation monitoring endpoints"
```

---

## Verification

1. `dotnet build Asterisk.Platform.slnx` — 0 warnings
2. `dotnet test Asterisk.Platform.slnx` — all tests pass (~1,582 + ~17 new ≈ ~1,599)
3. Canned responses: CRUD + search work, duplicate shortcut rejected
4. Supervisor: list/messages/takeover/close/note endpoints functional

## Estimated Scope
- ~6 new files, ~4 modified files
- ~17 new tests
- Migration 014 (canned_responses table)
- 1 new endpoint group + 5 endpoints added to existing group
