using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.TestSupport;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Tests;

public sealed class QueueWrapUpDurationRefresherTests
{
    private const string ServerId = "primary";
    private const string TenantA = "acme-eu";
    private const string TenantB = "globex";
    private const int PageSize = QueueStoreExtensions.EnumerationPageSize;

    private readonly InMemoryTenantStore _tenants = new();
    private readonly InMemoryQueueStore _queues = new();
    private readonly CapturingLogger _logger = new();
    private readonly QueueWrapUpDurationProvider _provider;

    public QueueWrapUpDurationRefresherTests()
    {
        _provider = new QueueWrapUpDurationProvider(_logger.For<QueueWrapUpDurationProvider>());
    }

    [Fact]
    public async Task RefreshAsync_ShouldLoadOnlyActiveTenants_WhenSeveralExist()
    {
        await AddTenantAsync(TenantA);
        await AddTenantAsync(TenantB);
        await AddTenantAsync("gone", TenantStatus.Suspended);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await SaveQueueAsync(TenantB, "q-1", "sales", 18);
        await SaveQueueAsync("gone", "q-1", "sales", 24);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
        Answer("globex-sales").Should().Be(TimeSpan.FromSeconds(18));
        Answer("gone-sales").Should().BeNull("a tenant no longer active falls out at the next pass");
    }

    [Fact]
    public async Task RefreshAsync_ShouldLoadEveryPage_WhenTheStoreHoldsMoreThanOnePage()
    {
        await AddTenantAsync(TenantA);
        for (var i = 0; i < (PageSize * 2) + 1; i++)
        {
            await SaveQueueAsync(TenantA, $"q-{i:D4}", $"n-{i:D4}", i % 600);
        }

        await Refresher().RefreshAsync(CancellationToken.None);

        for (var i = 0; i < (PageSize * 2) + 1; i++)
        {
            Answer($"acme-eu-n-{i:D4}").Should().Be(TimeSpan.FromSeconds(i % 600));
        }
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepTheConfiguredValue_WhenTheQueueIsInactive()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12, isActive: false);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task RefreshAsync_ShouldLeaveTheQueueAbsent_WhenItIsGoneFromTheStore()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await SaveQueueAsync(TenantA, "q-2", "support", 15);
        var refresher = Refresher();
        await refresher.RefreshAsync(CancellationToken.None);
        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));

        await _queues.DeleteAsync(new TenantId(TenantA), EntityId.From("q-1"), CancellationToken.None);
        await refresher.RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().BeNull();
        Answer("acme-eu-support").Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RefreshAsync_ShouldLeaveTheLastMapWhole_WhenTheStoreThrowsMidPass()
    {
        await AddTenantAsync(TenantA);
        await AddTenantAsync(TenantB);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await SaveQueueAsync(TenantB, "q-1", "sales", 18);
        await Refresher().RefreshAsync(CancellationToken.None);

        await SaveQueueAsync(TenantA, "q-1", "sales", 40);
        await SaveQueueAsync(TenantB, "q-1", "sales", 50);
        var failing = new ThrowAfterFirstPageQueueStore(_queues);

        var act = () => Refresher(failing).RefreshAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        failing.PagesRead.Should().Be(2, "the pass must have read one tenant before the store failed on the other");
        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
        Answer("globex-sales").Should().Be(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public async Task RefreshAsync_ShouldLoadEverySurvivor_WhenAFirstPageQueueIsDeletedBetweenPages()
    {
        await AddTenantAsync(TenantA);
        var ids = new List<string>();
        for (var i = 0; i < PageSize + 200; i++)
        {
            var id = $"q-{i:D4}";
            ids.Add(id);
            await SaveQueueAsync(TenantA, id, $"n-{i:D4}", 12);
        }
        var deleting = new DeleteAfterFirstPageQueueStore(_queues);

        await Refresher(deleting).RefreshAsync(CancellationToken.None);

        deleting.DeletedQueueId.Should().NotBeNull("the control must have deleted a first-page queue");
        var deleted = deleting.DeletedQueueId!.Value.Value;
        var missing = ids.Where(id => id != deleted)
            .Where(id => Answer($"acme-eu-n-{id[2..]}") is null)
            .ToList();
        missing.Should().BeEmpty("a queue that existed for the whole pass must be loaded");
    }

    [Theory]
    [InlineData(12, 20)]
    [InlineData(20, 12)]
    public async Task RefreshAsync_ShouldAnswerNoValueAndWarnOnce_WhenTwoQueuesShareAKeyWithDifferentValues(
        int firstListed, int secondListed)
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", firstListed == 12 ? "sales" : "SALES", firstListed);
        await SaveQueueAsync(TenantA, "q-2", secondListed == 12 ? "sales" : "SALES", secondListed);
        await SaveQueueAsync(TenantA, "q-3", "support", 15);
        var refresher = Refresher();

        await refresher.RefreshAsync(CancellationToken.None);
        await refresher.RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().BeNull();
        Answer("ACME-EU-SALES").Should().BeNull();
        Answer("acme-eu-support").Should().Be(TimeSpan.FromSeconds(15));
        _logger.Warnings.Should().ContainSingle().Which.Should().ContainEquivalentOf("acme-eu-sales");
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepTheValue_WhenTwoQueuesShareAKeyAndAValue()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await SaveQueueAsync(TenantA, "q-2", "SALES", 12);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
        _logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ShouldAnswerNoValue_WhenAThirdQueueAgreesWithOneOfTwoThatDiffer()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await SaveQueueAsync(TenantA, "q-2", "sales", 20);
        await SaveQueueAsync(TenantA, "q-3", "sales", 12);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().BeNull();
        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_ShouldAnswerNoValue_WhenTheTwoQueuesOfAKeySitOnDifferentPages()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-0000", "sales", 12);
        for (var i = 1; i < PageSize; i++)
        {
            await SaveQueueAsync(TenantA, $"q-{i:D4}", $"n-{i:D4}", 15);
        }
        await SaveQueueAsync(TenantA, $"q-{PageSize:D4}", "SALES", 20);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().BeNull();
        Answer("acme-eu-n-0001").Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RefreshAsync_ShouldAnswerNoValue_WhenQueuesOfTwoTenantsCollideAcrossTheSeparator()
    {
        await AddTenantAsync("a");
        await AddTenantAsync("a-b");
        await SaveQueueAsync("a", "q-1", "b-c", 12);
        await SaveQueueAsync("a-b", "q-1", "c", 20);

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("a-b-c").Should().BeNull();
    }

    [Fact]
    public async Task RefreshAsync_ShouldWinForOneTick_WhenThePassReadTheStoreBeforeASave()
    {
        await AddTenantAsync(TenantA);
        await SaveQueueAsync(TenantA, "q-1", "sales", 12);
        await Refresher().RefreshAsync(CancellationToken.None);
        var gated = new GatedAfterReadQueueStore(_queues);
        var decorator = new RealtimeSyncingQueueStore(
            _queues, Substitute.For<IRealtimeSyncService>(), _provider, NullLogger<RealtimeSyncingQueueStore>.Instance);

        var pass = Refresher(gated).RefreshAsync(CancellationToken.None);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await decorator.SaveAsync(QueueWrapUpRefresherRig.Queue(TenantA, "q-1", "sales", 18), CancellationToken.None);

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(18), "a save applies at once");

        gated.Release();
        await pass.WaitAsync(TimeSpan.FromSeconds(5));

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12), "the pass read the store before the save");

        await Refresher().RefreshAsync(CancellationToken.None);

        Answer("acme-eu-sales").Should().Be(TimeSpan.FromSeconds(18));
    }

    private QueueWrapUpDurationRefresher Refresher(IQueueStore? queues = null)
        => QueueWrapUpRefresherRig.Build(_tenants, queues ?? _queues, _provider, _logger.For<QueueWrapUpDurationRefresher>());

    private TimeSpan? Answer(string queueName) => _provider.GetWrapUpDuration(ServerId, queueName);

    private async Task AddTenantAsync(string tenantId, TenantStatus status = TenantStatus.Active)
        => await _tenants.UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Type = TenantType.Customer,
            Status = status,
        });

    private Task SaveQueueAsync(string tenantId, string queueId, string name, int seconds, bool isActive = true)
        => _queues.SaveAsync(QueueWrapUpRefresherRig.Queue(tenantId, queueId, name, seconds, isActive), CancellationToken.None);

    /// <summary>Reads the first page, then refuses every read after it, as a store that dies during a pass.</summary>
    private sealed class ThrowAfterFirstPageQueueStore(IQueueStore inner) : IQueueStore
    {
        public int PagesRead { get; private set; }

        public async Task<IReadOnlyList<Queue>> ListPageAfterAsync(
            TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct)
        {
            if (++PagesRead > 1)
            {
                throw new InvalidOperationException("the store went away");
            }

            return await inner.ListPageAfterAsync(tenantId, afterQueueId, limit, ct);
        }

        public Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
            => ++PagesRead > 1
                ? throw new InvalidOperationException("the store went away")
                : inner.ListAsync(tenantId, query, ct);

        public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
            => inner.GetByIdAsync(tenantId, queueId, ct);

        public Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct)
            => inner.GetByNameAsync(tenantId, name, ct);

        public Task SaveAsync(Queue queue, CancellationToken ct) => inner.SaveAsync(queue, ct);

        public Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
            => inner.DeleteAsync(tenantId, queueId, ct);
    }

    /// <summary>Collects the Warnings of the provider and of the refresher, so "one Warning" counts both.</summary>
    private sealed class CapturingLogger
    {
        public List<string> Warnings { get; } = [];

        public ILogger<T> For<T>() => new Typed<T>(Warnings);

        private sealed class Typed<T>(List<string> warnings) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}

/// <summary>A refresher over the given stores, resolved from a scope as the host resolves them.</summary>
internal static class QueueWrapUpRefresherRig
{
    public static QueueWrapUpDurationRefresher Build(
        ITenantStore tenants,
        IQueueStore queues,
        QueueWrapUpDurationProvider provider,
        ILogger<QueueWrapUpDurationRefresher>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tenants);
        services.AddSingleton(queues);
        return new QueueWrapUpDurationRefresher(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            provider,
            logger ?? NullLogger<QueueWrapUpDurationRefresher>.Instance);
    }

    public static Queue Queue(string tenantId, string queueId, string name, int seconds, bool isActive = true) => new()
    {
        QueueId = EntityId.From(queueId),
        TenantId = new TenantId(tenantId),
        Name = name,
        IsActive = isActive,
        WrapUp = new WrapUpConfig { DefaultWrapUpSeconds = seconds },
        CreatedAt = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero),
    };
}

/// <summary>
/// Holds a pass on a gate right after a page was read from the store: the store is reached, and the pass has not
/// finished, until <see cref="Release"/>.
/// </summary>
internal sealed class GatedAfterReadQueueStore(IQueueStore inner) : IQueueStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the pass has read the store and is waiting on the gate.</summary>
    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public async Task<IReadOnlyList<Queue>> ListPageAfterAsync(
        TenantId tenantId, EntityId? afterQueueId, int limit, CancellationToken ct)
    {
        var page = await inner.ListPageAfterAsync(tenantId, afterQueueId, limit, ct);
        _entered.TrySetResult();
        await _released.Task.WaitAsync(ct);
        return page;
    }

    public async Task<PagedResult<Queue>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var page = await inner.ListAsync(tenantId, query, ct);
        _entered.TrySetResult();
        await _released.Task.WaitAsync(ct);
        return page;
    }

    public Task<Queue?> GetByIdAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => inner.GetByIdAsync(tenantId, queueId, ct);

    public Task<Queue?> GetByNameAsync(TenantId tenantId, string name, CancellationToken ct)
        => inner.GetByNameAsync(tenantId, name, ct);

    public Task SaveAsync(Queue queue, CancellationToken ct) => inner.SaveAsync(queue, ct);

    public Task DeleteAsync(TenantId tenantId, EntityId queueId, CancellationToken ct)
        => inner.DeleteAsync(tenantId, queueId, ct);
}
