using Verbara.Platform.Audit;
using Verbara.Platform.Core;

namespace Verbara.Platform.Audit.Tests;

public class DefaultAuditServiceTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly TenantId Tenant2 = new("tenant-2");
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static (DefaultAuditService Service, IAuditStore Store) Build()
    {
        var store = Substitute.For<IAuditStore>();
        store.SaveAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultAuditService(store, clock);
        return (service, store);
    }

#pragma warning disable CS0618
    [Fact]
    public async Task LogAsync_ShouldSaveEntry_WithAllFields()
    {
        var (service, store) = Build();
        var details = new Dictionary<string, string> { ["reason"] = "new" };

        await service.LogAsync(Tenant1, "conversation.created", "Conversation", "conv-1", "user-42", details, CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e =>
                e.TenantId == Tenant1 &&
                e.Action == "conversation.created" &&
                e.TargetType == "Conversation" &&
                e.TargetId == "conv-1" &&
                e.ActorId == "user-42" &&
                e.OccurredAt == FixedNow &&
                e.Metadata != null && e.Metadata["reason"] == "new"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldGenerateNonEmptyEntryId()
    {
        var (service, store) = Build();

        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e => !string.IsNullOrWhiteSpace(e.EntryId.Value)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldSetActorIdToSystem_WhenPerformedByNotSupplied()
    {
        var (service, store) = Build();

        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e => e.ActorId == "system"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldUseClockTime()
    {
        var (service, store) = Build();

        await service.LogAsync(Tenant1, "agent.state_changed", "Agent", "agent-1", ct: CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e => e.OccurredAt == FixedNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldCallSaveOnce_PerCall()
    {
        var (service, store) = Build();

        await service.LogAsync(Tenant1, "conversation.created", "Conversation", "conv-1", ct: CancellationToken.None);
        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);

        await store.Received(2).SaveAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldGenerateUniqueEntryIds_ForEachCall()
    {
        var capturedIds = new List<string>();

        var store = Substitute.For<IAuditStore>();
        store.SaveAsync(Arg.Do<AuditEntry>(e => capturedIds.Add(e.EntryId.Value)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultAuditService(store, clock);

        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);
        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);

        capturedIds.Should().HaveCount(2);
        capturedIds[0].Should().NotBe(capturedIds[1]);
    }
#pragma warning restore CS0618

    [Theory]
    [InlineData("login.success", "auth")]
    [InlineData("logout", "auth")]
    [InlineData("mfa.enrolled", "auth")]
    [InlineData("session.revoked", "auth")]
    [InlineData("lockout.triggered", "auth")]
    [InlineData("role.assigned", "rbac")]
    [InlineData("permission.revoked", "rbac")]
    [InlineData("recording.downloaded", "data_access")]
    [InlineData("transcript.exported", "data_access")]
    [InlineData("api_key.created", "admin")]
    [InlineData("tenant.updated", "admin")]
    [InlineData("license.applied", "admin")]
    [InlineData("system.restart", "admin")]
    [InlineData("conversation.created", "config")]
    [InlineData("message.sent", "config")]
    public async Task RecordAsync_ShouldInferCorrectCategory_WhenCalledViaLogAsync(string action, string expectedCategory)
    {
        var (service, store) = Build();

#pragma warning disable CS0618
        await service.LogAsync(Tenant1, action, "Entity", "id-1", ct: CancellationToken.None);
#pragma warning restore CS0618

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e => e.Category == expectedCategory),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ShouldSaveEntry_WithAllNewFields()
    {
        var (service, store) = Build();
        var correlationId = Guid.NewGuid();
        var changes = new AuditChanges(Before: new { Name = "old" }, After: new { Name = "new" });
        var metadata = new Dictionary<string, string> { ["ip"] = "10.0.0.1" };

        await service.RecordAsync(
            Tenant1, "auth", "login.success", "info",
            actorId: "user-42", actorType: "user",
            targetId: "session-1", targetType: "Session",
            correlationId: correlationId, changes: changes,
            metadata: metadata, ct: CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e =>
                e.TenantId == Tenant1 &&
                e.Action == "login.success" &&
                e.Category == "auth" &&
                e.Severity == "info" &&
                e.ActorId == "user-42" &&
                e.ActorType == "user" &&
                e.TargetId == "session-1" &&
                e.TargetType == "Session" &&
                e.CorrelationId == correlationId &&
                e.Changes == changes &&
                e.Metadata != null && e.Metadata["ip"] == "10.0.0.1" &&
                e.OccurredAt == FixedNow),
            Arg.Any<CancellationToken>());
    }
}

public class AuditQueryTests
{
    [Fact]
    public void AuditQuery_ShouldHaveDefaults()
    {
        var query = new AuditQuery();

        query.Action.Should().BeNull();
        query.EntityType.Should().BeNull();
        query.PerformedBy.Should().BeNull();
        query.From.Should().BeNull();
        query.To.Should().BeNull();
        query.Page.Should().Be(1);
        query.PageSize.Should().Be(50);
        query.Category.Should().BeNull();
        query.Severity.Should().BeNull();
        query.ActorId.Should().BeNull();
        query.TargetId.Should().BeNull();
        query.TargetType.Should().BeNull();
        query.CorrelationId.Should().BeNull();
    }

    [Fact]
    public void AuditQuery_ShouldAcceptAllFilters()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-7);
        var to = DateTimeOffset.UtcNow;
        var query = new AuditQuery("conversation.created", "Conversation", "user-1", from, to, 2, 25);

        query.Action.Should().Be("conversation.created");
        query.EntityType.Should().Be("Conversation");
        query.PerformedBy.Should().Be("user-1");
        query.From.Should().Be(from);
        query.To.Should().Be(to);
        query.Page.Should().Be(2);
        query.PageSize.Should().Be(25);
    }

    [Fact]
    public void AuditQuery_ShouldAcceptNewFields()
    {
        var correlationId = Guid.NewGuid();
        var query = new AuditQuery(
            Category: "auth",
            Severity: "critical",
            ActorId: "user-99",
            TargetId: "session-1",
            TargetType: "Session",
            CorrelationId: correlationId);

        query.Category.Should().Be("auth");
        query.Severity.Should().Be("critical");
        query.ActorId.Should().Be("user-99");
        query.TargetId.Should().Be("session-1");
        query.TargetType.Should().Be("Session");
        query.CorrelationId.Should().Be(correlationId);
    }
}

public class AuditChangesTests
{
    [Fact]
    public void AuditChanges_ShouldStoreBeforeAndAfter()
    {
        var before = new { Name = "old-name" };
        var after = new { Name = "new-name" };

        var changes = new AuditChanges(Before: before, After: after);

        changes.Before.Should().Be(before);
        changes.After.Should().Be(after);
    }

    [Fact]
    public void AuditChanges_ShouldAllowNullBeforeForCreation()
    {
        var after = new { Status = "active" };

        var changes = new AuditChanges(Before: null, After: after);

        changes.Before.Should().BeNull();
        changes.After.Should().Be(after);
    }

    [Fact]
    public void AuditChanges_ShouldAllowNullAfterForDeletion()
    {
        var before = new { Status = "active" };

        var changes = new AuditChanges(Before: before, After: null);

        changes.Before.Should().Be(before);
        changes.After.Should().BeNull();
    }

    [Fact]
    public void AuditChanges_ShouldSupportValueEquality()
    {
        var a = new AuditChanges(Before: "x", After: "y");
        var b = new AuditChanges(Before: "x", After: "y");

        a.Should().Be(b);
    }
}

public class AuditEntryBackwardCompatTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

#pragma warning disable CS0618
    [Fact]
    public void EntityType_ShouldReturnTargetType()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "conversation.created",
            TargetType = "Conversation",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.EntityType.Should().Be("Conversation");
    }

    [Fact]
    public void EntityType_ShouldInitTargetType_WhenSet()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "conversation.created",
            EntityType = "Conversation",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.TargetType.Should().Be("Conversation");
    }

    [Fact]
    public void EntityType_ShouldReturnEmptyString_WhenTargetTypeIsNull()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "message.sent",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.EntityType.Should().BeEmpty();
    }

    [Fact]
    public void EntityId_ShouldReturnTargetId()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "conversation.created",
            TargetId = "conv-42",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.EntityId.Should().Be("conv-42");
    }

    [Fact]
    public void PerformedBy_ShouldReturnActorId_WhenNotSystem()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "login.success",
            ActorId = "user-99",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.PerformedBy.Should().Be("user-99");
    }

    [Fact]
    public void PerformedBy_ShouldReturnNull_WhenActorIsSystem()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "system.restart",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.PerformedBy.Should().BeNull();
    }

    [Fact]
    public void PerformedBy_ShouldInitActorId_WhenSet()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "role.assigned",
            PerformedBy = "admin-1",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ActorId.Should().Be("admin-1");
    }

    [Fact]
    public void PerformedBy_ShouldSetActorIdToSystem_WhenSetToNull()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "system.restart",
            PerformedBy = null,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.ActorId.Should().Be("system");
    }

    [Fact]
    public void Details_ShouldReturnMetadata()
    {
        var metadata = new Dictionary<string, string> { ["ip"] = "10.0.0.1" };
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "login.success",
            Metadata = metadata,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.Details.Should().ContainKey("ip").WhoseValue.Should().Be("10.0.0.1");
    }

    [Fact]
    public void Details_ShouldInitMetadata_WhenSet()
    {
        var details = new Dictionary<string, string> { ["reason"] = "new" };
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "conversation.created",
            Details = details,
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.Metadata.Should().ContainKey("reason").WhoseValue.Should().Be("new");
    }
#pragma warning restore CS0618
}

public class AuditEntryTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void AuditEntry_ShouldHoldAllNewProperties()
    {
        var id = EntityId.New();
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var correlationId = Guid.NewGuid();
        var changes = new AuditChanges(Before: null, After: "x");

        var entry = new AuditEntry
        {
            EntryId = id,
            TenantId = Tenant1,
            Action = "role.assigned",
            Category = "rbac",
            Severity = "warning",
            ActorId = "user-1",
            ActorType = "user",
            TargetId = "role-99",
            TargetType = "Role",
            CorrelationId = correlationId,
            Changes = changes,
            Metadata = metadata,
            OccurredAt = now,
        };

        entry.EntryId.Should().Be(id);
        entry.TenantId.Should().Be(Tenant1);
        entry.Action.Should().Be("role.assigned");
        entry.Category.Should().Be("rbac");
        entry.Severity.Should().Be("warning");
        entry.ActorId.Should().Be("user-1");
        entry.ActorType.Should().Be("user");
        entry.TargetId.Should().Be("role-99");
        entry.TargetType.Should().Be("Role");
        entry.CorrelationId.Should().Be(correlationId);
        entry.Changes.Should().Be(changes);
        entry.Metadata.Should().ContainKey("k").WhoseValue.Should().Be("v");
        entry.OccurredAt.Should().Be(now);
    }

    [Fact]
    public void AuditEntry_ShouldHaveDefaults_ForOptionalNewFields()
    {
        var entry = new AuditEntry
        {
            EntryId = EntityId.New(),
            TenantId = Tenant1,
            Action = "message.sent",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        entry.Category.Should().Be("config");
        entry.Severity.Should().Be("info");
        entry.ActorId.Should().Be("system");
        entry.ActorType.Should().Be("system");
        entry.TargetId.Should().BeNull();
        entry.TargetType.Should().BeNull();
        entry.CorrelationId.Should().BeNull();
        entry.Changes.Should().BeNull();
        entry.Metadata.Should().BeNull();
    }
}

public class DefaultAuditServiceIntegrityHashTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private static (DefaultAuditService Service, List<AuditEntry> Captured) Build()
    {
        var captured = new List<AuditEntry>();
        var store = Substitute.For<IAuditStore>();
        store.SaveAsync(Arg.Do<AuditEntry>(e => captured.Add(e)), Arg.Any<CancellationToken>())
             .Returns(Task.CompletedTask);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(FixedNow);

        var service = new DefaultAuditService(store, clock);
        return (service, captured);
    }

    [Fact]
    public async Task RecordAsync_ShouldHaveDeterministicIntegrityHash_WhenRecorded()
    {
        // Two identical RecordAsync calls (same clock, same fields) must produce the
        // same hash — the hash is a pure function of the identifying fields.
        var (service, captured) = Build();

        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            metadata: new Dictionary<string, string> { ["confidence"] = "0.92", ["accepted"] = "true" },
            ct: CancellationToken.None);

        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            metadata: new Dictionary<string, string> { ["confidence"] = "0.92", ["accepted"] = "true" },
            ct: CancellationToken.None);

        captured.Should().HaveCount(2);
        captured[0].IntegrityHash.Should().NotBeNullOrEmpty();
        captured[1].IntegrityHash.Should().NotBeNullOrEmpty();
        // Same logical entry → identical hash.
        captured[0].IntegrityHash.Should().Be(captured[1].IntegrityHash);
    }

    [Fact]
    public async Task RecordAsync_ShouldHaveDifferentIntegrityHash_WhenFieldChanges()
    {
        var (service, captured) = Build();

        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            ct: CancellationToken.None);

        // Change only the targetId — hash must differ.
        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-2", targetType: "Conversation",
            ct: CancellationToken.None);

        captured.Should().HaveCount(2);
        captured[0].IntegrityHash.Should().NotBeNullOrEmpty();
        captured[1].IntegrityHash.Should().NotBeNullOrEmpty();
        captured[0].IntegrityHash.Should().NotBe(captured[1].IntegrityHash);
    }

    [Fact]
    public async Task RecordAsync_ShouldHaveNonNullIntegrityHash_ForAllEntries()
    {
        var (service, captured) = Build();

        await service.RecordAsync(
            Tenant1, "auth", "login.success", "info",
            actorId: "user-99", actorType: "user",
            ct: CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].IntegrityHash.Should().NotBeNull();
        // Lowercase hex SHA-256 is always 64 characters.
        captured[0].IntegrityHash!.Length.Should().Be(64);
        captured[0].IntegrityHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task RecordAsync_ShouldHaveDeterministicIntegrityHash_WhenMetadataInsertedInDifferentOrder()
    {
        // Metadata key/value pairs are identical but inserted in reverse order.
        // The hash must be the same because keys are sorted (Ordinal) before encoding.
        var (service, captured) = Build();

        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            metadata: new Dictionary<string, string>
            {
                ["accepted"] = "true",
                ["confidence"] = "0.92",
                ["modelId"] = "gpt-4o",
            },
            ct: CancellationToken.None);

        await service.RecordAsync(
            Tenant1, "conversations", "typification.ai_disposition", "info",
            actorId: "user-42", actorType: "user",
            targetId: "conv-1", targetType: "Conversation",
            metadata: new Dictionary<string, string>
            {
                ["modelId"] = "gpt-4o",
                ["confidence"] = "0.92",
                ["accepted"] = "true",
            },
            ct: CancellationToken.None);

        captured.Should().HaveCount(2);
        captured[0].IntegrityHash.Should().NotBeNullOrEmpty();
        captured[1].IntegrityHash.Should().NotBeNullOrEmpty();
        // Insertion order must not affect the hash.
        captured[0].IntegrityHash.Should().Be(captured[1].IntegrityHash);
    }
}
