using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Platform.Realtime.Services;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class RelayOutcomeSinkTests
{
    private static RelayOutcomeEntry MakeEntry(
        DateTimeOffset ts,
        RelayOutcomeKind outcome = RelayOutcomeKind.Forwarded,
        string eventType = "test.event",
        string? tenantId = "acme") =>
        new(
            Ts: ts,
            EventType: eventType,
            TenantId: tenantId,
            Resource: "realtime:fanout:leader",
            Outcome: outcome,
            IsLeaderAtTime: outcome == RelayOutcomeKind.Forwarded,
            LeaderInstanceId: "leader-1",
            PodInstanceId: null,
            DetailMessage: null);

    [Fact]
    public void Record_ShouldStampPodInstanceIdFromOptions_WhenCallerPassesNull()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-xyz",
        });

        sink.Record(MakeEntry(DateTimeOffset.UtcNow));

        var page = sink.Snapshot(since: null, limit: 10);
        page.PodInstanceId.Should().Be("pod-xyz");
        page.Entries.Should().ContainSingle();
        page.Entries[0].PodInstanceId.Should().Be("pod-xyz");
    }

    [Fact]
    public void Snapshot_ShouldReturnEntriesInChronologicalOrder()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-a",
        });

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            sink.Record(MakeEntry(t0.AddMilliseconds(i), eventType: $"evt-{i}"));
        }

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().HaveCount(5);
        page.Entries.Select(e => e.EventType).Should().BeInAscendingOrder();
        page.CurrentSize.Should().Be(5);
        page.EvictedSinceStart.Should().Be(0);
    }

    [Fact]
    public void Snapshot_ShouldFilterByExclusiveSinceCursor()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-a",
        });

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            sink.Record(MakeEntry(t0.AddSeconds(i), eventType: $"evt-{i}"));
        }

        var cursor = t0.AddSeconds(2);
        var page = sink.Snapshot(since: cursor, limit: 100);
        page.Entries.Should().HaveCount(2);
        page.Entries.Select(e => e.EventType).Should().BeEquivalentTo(["evt-3", "evt-4"], opt => opt.WithStrictOrdering());
    }

    [Fact]
    public void Snapshot_ShouldClampLimitToCapacity_WhenLimitExceedsBuffer()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-a",
        });

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 50; i++)
        {
            sink.Record(MakeEntry(t0.AddMilliseconds(i)));
        }

        var page = sink.Snapshot(since: null, limit: 1_000_000);
        page.Entries.Should().HaveCount(50);
    }

    [Fact]
    public void Record_ShouldEvictOldestAndIncrementEvictedCounter_WhenCapacityExceeded()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-a",
        });

        var t0 = DateTimeOffset.UtcNow;
        for (var i = 0; i < 250; i++)
        {
            sink.Record(MakeEntry(t0.AddMilliseconds(i), eventType: $"evt-{i:D3}"));
        }

        var page = sink.Snapshot(since: null, limit: 1_000);
        page.CurrentSize.Should().Be(100);
        page.EvictedSinceStart.Should().Be(150);
        page.OldestRetainedTs.Should().Be(t0.AddMilliseconds(150));
        page.Entries.Should().HaveCount(100);
        page.Entries[0].EventType.Should().Be("evt-150");
        page.Entries[^1].EventType.Should().Be("evt-249");
    }

    [Fact]
    public void Snapshot_ShouldReturnEmptyPageWithMetadata_WhenBufferIsEmpty()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 100,
            PodInstanceId = "pod-empty",
        });

        var page = sink.Snapshot(since: null, limit: 10);
        page.Entries.Should().BeEmpty();
        page.CurrentSize.Should().Be(0);
        page.EvictedSinceStart.Should().Be(0);
        page.OldestRetainedTs.Should().BeNull();
        page.PodInstanceId.Should().Be("pod-empty");
        page.Capacity.Should().Be(100);
    }

    [Fact]
    public async Task Record_ShouldNotLoseEntries_UnderConcurrentWriters()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 10_000,
            PodInstanceId = "pod-concurrency",
        });

        const int writerCount = 16;
        const int entriesPerWriter = 500;
        var t0 = DateTimeOffset.UtcNow;

        var writers = Enumerable.Range(0, writerCount).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < entriesPerWriter; i++)
                {
                    sink.Record(MakeEntry(t0.AddTicks(workerId * entriesPerWriter + i), eventType: $"w{workerId}-i{i}"));
                }
            })).ToArray();

        await Task.WhenAll(writers);

        var page = sink.Snapshot(since: null, limit: writerCount * entriesPerWriter);
        page.CurrentSize.Should().Be(writerCount * entriesPerWriter);
        page.EvictedSinceStart.Should().Be(0);
        page.Entries.Should().HaveCount(writerCount * entriesPerWriter);
    }

    [Fact]
    public async Task Snapshot_ShouldNotThrow_WhenInvokedConcurrentlyWithWriters()
    {
        using var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 5_000,
            PodInstanceId = "pod-race",
        });

        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var writer = Task.Run(async () =>
        {
            var t0 = DateTimeOffset.UtcNow;
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                sink.Record(MakeEntry(t0.AddTicks(i++)));
                if (i % 100 == 0) await Task.Yield();
            }
        });

        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = sink.Snapshot(since: null, limit: 100);
                await Task.Yield();
            }
        });

        await Task.WhenAll(writer, reader);
        // The fact that we didn't throw is the assertion. Confirm the sink is
        // still in a coherent state (CurrentSize ≤ Capacity, no negative
        // counters) after the race window.
        var page = sink.Snapshot(since: null, limit: int.MaxValue);
        page.CurrentSize.Should().BeInRange(1, 5_000);
        page.EvictedSinceStart.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenCapacityBelowMinimum()
    {
        var act = () => _ = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 50,
            PodInstanceId = "pod-bad",
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenCapacityAboveMaximum()
    {
        var act = () => _ = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 500_000,
            PodInstanceId = "pod-bad",
        });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
