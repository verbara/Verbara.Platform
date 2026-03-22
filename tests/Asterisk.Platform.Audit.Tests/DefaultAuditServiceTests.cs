using Asterisk.Platform.Audit;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Audit.Tests;

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
                e.EntityType == "Conversation" &&
                e.EntityId == "conv-1" &&
                e.PerformedBy == "user-42" &&
                e.OccurredAt == FixedNow &&
                e.Details != null && e.Details["reason"] == "new"),
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
    public async Task LogAsync_ShouldSetPerformedByToNull_WhenNotSupplied()
    {
        var (service, store) = Build();

        await service.LogAsync(Tenant1, "message.sent", "Message", "msg-1", ct: CancellationToken.None);

        await store.Received(1).SaveAsync(
            Arg.Is<AuditEntry>(e => e.PerformedBy == null),
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
}

public class AuditEntryTests
{
    private static readonly TenantId Tenant1 = new("tenant-1");

    [Fact]
    public void AuditEntry_ShouldHoldAllProperties()
    {
        var id = EntityId.New();
        var now = DateTimeOffset.UtcNow;
        var details = new Dictionary<string, string> { ["k"] = "v" };

        var entry = new AuditEntry
        {
            EntryId = id,
            TenantId = Tenant1,
            Action = "message.sent",
            EntityType = "Message",
            EntityId = "msg-1",
            PerformedBy = "system",
            Details = details,
            OccurredAt = now,
        };

        entry.EntryId.Should().Be(id);
        entry.TenantId.Should().Be(Tenant1);
        entry.Action.Should().Be("message.sent");
        entry.EntityType.Should().Be("Message");
        entry.EntityId.Should().Be("msg-1");
        entry.PerformedBy.Should().Be("system");
        entry.Details.Should().ContainKey("k").WhoseValue.Should().Be("v");
        entry.OccurredAt.Should().Be(now);
    }
}
