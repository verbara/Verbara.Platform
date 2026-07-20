using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0012 Ola-3 — unit tests for the best-effort realtime-sync store decorators
/// (<see cref="RealtimeSyncingQueueStore"/>, <see cref="RealtimeSyncingAgentStore"/>,
/// <see cref="RealtimeSyncingQueueMembershipStore"/>). Each write method is verified on three axes:
/// (1) it delegates to the inner store with the exact entity regardless of the sync outcome;
/// (2) it fires the mapped best-effort sync on success; (3) it swallows a sync throw and logs a
/// Warning (EventId 4130) so the write still completes. The agent decorator additionally skips the
/// sync when the agent has no SIP identity.
/// </summary>
public sealed class RealtimeSyncingStoreDecoratorTests
{
    private const string Tenant = "t-dec";
    private const int DeferralEventId = 4130;

    // ─── Queue decorator ─────────────────────────────────────────────────────

    [Fact]
    public async Task QueueSave_ShouldDelegateToInnerAndSync_WhenSyncSucceeds()
    {
        var inner = new InMemoryQueueStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new RealtimeSyncingQueueStore(inner, sync, NullLogger<RealtimeSyncingQueueStore>.Instance);
        var queue = MakeQueue();

        await sut.SaveAsync(queue, CancellationToken.None);

        (await inner.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None))
            .Should().NotBeNull("the write must delegate to the inner store");
        await sync.Received(1).SyncQueueAsync(Tenant, queue.Name,
            Arg.Is<RealtimeQueueOptions>(o => o.Maxlen == 7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueSave_ShouldStillPersist_WhenSyncThrows()
    {
        var inner = new InMemoryQueueStore();
        var sync = Substitute.For<IRealtimeSyncService>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.SyncQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RealtimeQueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Faulted());
#pragma warning restore CA2012
        var logger = new CapturingLogger<RealtimeSyncingQueueStore>();
        var sut = new RealtimeSyncingQueueStore(inner, sync, logger);
        var queue = MakeQueue();

        var act = async () => await sut.SaveAsync(queue, CancellationToken.None);

        await act.Should().NotThrowAsync("a sync throw is best-effort and must not fail the write");
        (await inner.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None)).Should().NotBeNull();
        logger.Warned(DeferralEventId).Should().BeTrue("the deferral must be logged at Warning (EventId 4130)");
    }

    [Fact]
    public async Task QueueDelete_ShouldRemoveViaSync_UsingResolvedName()
    {
        var inner = new InMemoryQueueStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new RealtimeSyncingQueueStore(inner, sync, NullLogger<RealtimeSyncingQueueStore>.Instance);
        var queue = MakeQueue();
        await inner.SaveAsync(queue, CancellationToken.None);

        await sut.DeleteAsync(queue.TenantId, queue.QueueId, CancellationToken.None);

        (await inner.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None)).Should().BeNull();
        await sync.Received(1).RemoveQueueAsync(Tenant, queue.Name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueueDelete_ShouldStillDelete_WhenSyncThrows()
    {
        var inner = new InMemoryQueueStore();
        var sync = Substitute.For<IRealtimeSyncService>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.RemoveQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Faulted());
#pragma warning restore CA2012
        var logger = new CapturingLogger<RealtimeSyncingQueueStore>();
        var sut = new RealtimeSyncingQueueStore(inner, sync, logger);
        var queue = MakeQueue();
        await inner.SaveAsync(queue, CancellationToken.None);

        var act = async () => await sut.DeleteAsync(queue.TenantId, queue.QueueId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await inner.GetByIdAsync(queue.TenantId, queue.QueueId, CancellationToken.None)).Should().BeNull();
        logger.Warned(DeferralEventId).Should().BeTrue();
    }

    // ─── Agent decorator ─────────────────────────────────────────────────────

    [Fact]
    public async Task AgentSave_ShouldSync_WhenExtensionAndPasswordPresent()
    {
        var inner = new InMemoryAgentStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new RealtimeSyncingAgentStore(inner, sync, NullLogger<RealtimeSyncingAgentStore>.Instance);
        var agent = MakeAgent(extension: "6001", sipPassword: "secret");

        await sut.SaveAsync(agent, CancellationToken.None);

        (await inner.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None)).Should().NotBeNull();
        await sync.Received(1).SyncAgentAsync(Tenant, agent.AgentId.Value, agent.DisplayName,
            "6001", "secret", Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentSave_ShouldNotSync_WhenExtensionOrPasswordMissing()
    {
        var inner = new InMemoryAgentStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new RealtimeSyncingAgentStore(inner, sync, NullLogger<RealtimeSyncingAgentStore>.Instance);
        var agent = MakeAgent(extension: null, sipPassword: null);

        await sut.SaveAsync(agent, CancellationToken.None);

        (await inner.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None)).Should().NotBeNull();
        await sync.DidNotReceiveWithAnyArgs().SyncAgentAsync(
            default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task AgentSave_ShouldStillPersist_WhenSyncThrows()
    {
        var inner = new InMemoryAgentStore();
        var sync = Substitute.For<IRealtimeSyncService>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.SyncAgentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Faulted());
#pragma warning restore CA2012
        var logger = new CapturingLogger<RealtimeSyncingAgentStore>();
        var sut = new RealtimeSyncingAgentStore(inner, sync, logger);
        var agent = MakeAgent(extension: "6002", sipPassword: "secret");

        var act = async () => await sut.SaveAsync(agent, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await inner.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None)).Should().NotBeNull();
        logger.Warned(DeferralEventId).Should().BeTrue();
    }

    [Fact]
    public async Task AgentDelete_ShouldRemoveViaSync()
    {
        var inner = new InMemoryAgentStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        var sut = new RealtimeSyncingAgentStore(inner, sync, NullLogger<RealtimeSyncingAgentStore>.Instance);
        var agent = MakeAgent(extension: "6003", sipPassword: "secret");
        await inner.SaveAsync(agent, CancellationToken.None);

        await sut.DeleteAsync(agent.TenantId, agent.AgentId, CancellationToken.None);

        (await inner.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None)).Should().BeNull();
        await sync.Received(1).RemoveAgentAsync(Tenant, agent.AgentId.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentDelete_ShouldStillDelete_WhenSyncThrows()
    {
        var inner = new InMemoryAgentStore();
        var sync = Substitute.For<IRealtimeSyncService>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.RemoveAgentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Faulted());
#pragma warning restore CA2012
        var logger = new CapturingLogger<RealtimeSyncingAgentStore>();
        var sut = new RealtimeSyncingAgentStore(inner, sync, logger);
        var agent = MakeAgent(extension: "6004", sipPassword: "secret");
        await inner.SaveAsync(agent, CancellationToken.None);

        var act = async () => await sut.DeleteAsync(agent.TenantId, agent.AgentId, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await inner.GetByIdAsync(agent.TenantId, agent.AgentId, CancellationToken.None)).Should().BeNull();
        logger.Warned(DeferralEventId).Should().BeTrue();
    }

    // ─── Membership decorator ────────────────────────────────────────────────

    [Fact]
    public async Task MembershipSave_ShouldSync_UsingResolvedQueueNameAndAgentDisplayName()
    {
        var (sut, membershipInner, sync, queue, agent) = BuildMembershipSut(out _);
        var membership = MakeMembership(agent.AgentId, queue.QueueId, penalty: 3);

        await sut.SaveAsync(membership, CancellationToken.None);

        (await membershipInner.GetAsync(membership.TenantId, queue.QueueId, agent.AgentId, CancellationToken.None))
            .Should().NotBeNull();
        await sync.Received(1).AddQueueMemberAsync(Tenant, queue.Name, agent.AgentId.Value,
            agent.DisplayName, 3, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MembershipSave_ShouldStillPersist_WhenSyncThrows()
    {
        var (sut, membershipInner, sync, queue, agent) = BuildMembershipSut(out var logger);
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        sync.AddQueueMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Faulted());
#pragma warning restore CA2012
        var membership = MakeMembership(agent.AgentId, queue.QueueId);

        var act = async () => await sut.SaveAsync(membership, CancellationToken.None);

        await act.Should().NotThrowAsync();
        (await membershipInner.GetAsync(membership.TenantId, queue.QueueId, agent.AgentId, CancellationToken.None))
            .Should().NotBeNull();
        logger.Warned(DeferralEventId).Should().BeTrue();
    }

    [Fact]
    public async Task MembershipDelete_ShouldRemoveViaSync()
    {
        var (sut, membershipInner, sync, queue, agent) = BuildMembershipSut(out _);
        await membershipInner.SaveAsync(MakeMembership(agent.AgentId, queue.QueueId), CancellationToken.None);

        await sut.DeleteAsync(new TenantId(Tenant), queue.QueueId, agent.AgentId, CancellationToken.None);

        (await membershipInner.GetAsync(new TenantId(Tenant), queue.QueueId, agent.AgentId, CancellationToken.None))
            .Should().BeNull();
        await sync.Received(1).RemoveQueueMemberAsync(Tenant, queue.Name, agent.AgentId.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MembershipBulkDelete_ShouldNotPerMemberSync()
    {
        var (sut, membershipInner, sync, queue, agent) = BuildMembershipSut(out _);
        await membershipInner.SaveAsync(MakeMembership(agent.AgentId, queue.QueueId), CancellationToken.None);

        await sut.DeleteAllForQueueAsync(new TenantId(Tenant), queue.QueueId, CancellationToken.None);
        await sut.DeleteAllForAgentAsync(new TenantId(Tenant), agent.AgentId, CancellationToken.None);

        await sync.DidNotReceiveWithAnyArgs().RemoveQueueMemberAsync(default!, default!, default!, default);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A faulted <see cref="ValueTask"/> that throws when awaited — models a fully
    /// unavailable realtime backend for the best-effort-deferral assertions.</summary>
    private static ValueTask Faulted() =>
        new(Task.FromException(new InvalidOperationException("boom")));

    private static (RealtimeSyncingQueueMembershipStore Sut, IQueueMembershipStore MembershipInner,
        IRealtimeSyncService Sync, Queue Queue, Agent Agent) BuildMembershipSut(
        out CapturingLogger<RealtimeSyncingQueueMembershipStore> logger)
    {
        var queueInner = new InMemoryQueueStore();
        var agentInner = new InMemoryAgentStore();
        var membershipInner = new InMemoryQueueMembershipStore();
        var sync = Substitute.For<IRealtimeSyncService>();
        logger = new CapturingLogger<RealtimeSyncingQueueMembershipStore>();

        var queue = MakeQueue();
        var agent = MakeAgent(extension: "6100", sipPassword: "secret");
        queueInner.SaveAsync(queue, CancellationToken.None).GetAwaiter().GetResult();
        agentInner.SaveAsync(agent, CancellationToken.None).GetAwaiter().GetResult();

        var sut = new RealtimeSyncingQueueMembershipStore(
            membershipInner, queueInner, agentInner, sync, logger);
        return (sut, membershipInner, sync, queue, agent);
    }

    private static Queue MakeQueue() => new()
    {
        QueueId = EntityId.New(),
        TenantId = new TenantId(Tenant),
        Name = "q-support",
        IsActive = true,
        MaxWaiting = 7,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static Agent MakeAgent(string? extension, string? sipPassword) => new()
    {
        AgentId = EntityId.New(),
        TenantId = new TenantId(Tenant),
        UserId = EntityId.New(),
        DisplayName = "Decorator Agent",
        State = AgentState.Available,
        Extension = extension,
        SipPassword = sipPassword,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static QueueMembership MakeMembership(EntityId agentId, EntityId queueId, int penalty = 0) => new()
    {
        TenantId = new TenantId(Tenant),
        QueueId = queueId,
        AgentId = agentId,
        Penalty = penalty,
        Source = MembershipSource.Manual,
        IsExcluded = false,
        CreatedAt = DateTimeOffset.UtcNow,
        AllowedChannels = null,
    };

    /// <summary>Minimal capturing logger — records the (level, eventId) of every log call so a test
    /// can assert a specific Warning EventId fired without an external test-logging package.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, int EventId)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, eventId.Id));

        public bool Warned(int eventId)
            => _entries.Any(e => e.Level == LogLevel.Warning && e.EventId == eventId);
    }
}
