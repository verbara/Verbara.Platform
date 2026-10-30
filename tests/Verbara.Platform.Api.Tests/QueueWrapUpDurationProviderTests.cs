using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

public sealed class QueueWrapUpDurationProviderTests
{
    private const string ServerId = "primary";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public void GetWrapUpDuration_ShouldAnswerTheQueueValue_WhenTenantIdContainsAHyphen()
    {
        var provider = ProviderHolding(("acme-eu", "sales", 12));

        provider.GetWrapUpDuration(ServerId, "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void GetWrapUpDuration_ShouldAnswerTheQueueValue_WhenNameArrivesInAnotherCase()
    {
        var provider = ProviderHolding(("acme-eu", "sales", 12));

        provider.GetWrapUpDuration(ServerId, "ACME-EU-SALES").Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void GetWrapUpDuration_ShouldAnswerTheQueueValue_WhenAskedFromAnyServerId()
    {
        var provider = ProviderHolding(("acme-eu", "sales", 12));

        provider.GetWrapUpDuration("primary", "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
        provider.GetWrapUpDuration("node-2", "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void GetWrapUpDuration_ShouldReturnNull_WhenQueueIsUnknown()
    {
        var provider = ProviderHolding(("acme-eu", "sales", 12));

        provider.GetWrapUpDuration(ServerId, "static-queue").Should().BeNull();
    }

    [Fact]
    public void GetWrapUpDuration_ShouldLogOneWarningPerName_WhenQueueIsUnknownAfterTheFirstLoad()
    {
        var logger = new CapturingLogger();
        var provider = new QueueWrapUpDurationProvider(logger);

        provider.GetWrapUpDuration(ServerId, "static-queue").Should().BeNull();
        logger.Warnings.Should().BeEmpty("nothing is known before the first load, so nothing is unknown");

        provider.Replace(Values(("acme-eu", "sales", 12)));

        provider.GetWrapUpDuration(ServerId, "static-queue").Should().BeNull();
        provider.GetWrapUpDuration(ServerId, "static-queue").Should().BeNull();
        provider.GetWrapUpDuration(ServerId, "STATIC-QUEUE").Should().BeNull();
        provider.GetWrapUpDuration(ServerId, "other-static-queue").Should().BeNull();

        logger.Warnings.Should().HaveCount(2);
        logger.Warnings[0].Should().Contain("static-queue");
        logger.Warnings[1].Should().Contain("other-static-queue");
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(900)]
    public void GetWrapUpDuration_ShouldReturnStoredValueUnchanged_WhenItIsOutOfRange(int storedSeconds)
    {
        var provider = ProviderHolding(("acme-eu", "sales", storedSeconds));

        provider.GetWrapUpDuration(ServerId, "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(storedSeconds));
    }

    [Fact]
    public async Task GetWrapUpDuration_ShouldAnswerFromTheLastSnapshot_WhenARefreshIsBlocked()
    {
        var provider = ProviderHolding(("acme-eu", "sales", 12));
        var tenants = new InMemoryTenantStore();
        await tenants.UpsertAsync(new Tenant { TenantId = "acme-eu", Name = "Acme", Type = TenantType.Customer });
        var queues = new InMemoryQueueStore();
        await queues.SaveAsync(QueueWrapUpRefresherRig.Queue("acme-eu", "q-1", "sales", 18), CancellationToken.None);
        var gated = new GatedAfterReadQueueStore(queues);
        var refresher = QueueWrapUpRefresherRig.Build(tenants, gated, provider);

        var refresh = refresher.RefreshAsync(CancellationToken.None);
        await gated.Entered.WaitAsync(Bound);

        var answer = await Task.Run(() => provider.GetWrapUpDuration(ServerId, "acme-eu-sales")).WaitAsync(Bound);

        answer.Should().Be(TimeSpan.FromSeconds(12));

        gated.Release();
        await refresh.WaitAsync(Bound);
        provider.GetWrapUpDuration(ServerId, "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(18));
    }

    [Fact]
    public void GetWrapUpDuration_ShouldNotWarn_WhenOnlyASaveLandedBeforeTheFirstLoad()
    {
        var logger = new CapturingLogger();
        var provider = new QueueWrapUpDurationProvider(logger);

        provider.Update("acme-eu-sales", TimeSpan.FromSeconds(18));

        provider.GetWrapUpDuration(ServerId, "acme-eu-sales").Should().Be(TimeSpan.FromSeconds(18));
        provider.GetWrapUpDuration(ServerId, "acme-eu-support").Should().BeNull();
        logger.Warnings.Should().BeEmpty("no queue is unknown until a whole pass has loaded them");
    }

    [Fact]
    public void Update_ShouldKeepEverySave_WhenSavesRace()
    {
        const int saves = 2000;
        var provider = ProviderHolding(("acme-eu", "sales", 12));

        Parallel.For(0, saves, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => provider.Update($"acme-eu-q-{i}", TimeSpan.FromSeconds(i % 600)));

        for (var i = 0; i < saves; i++)
        {
            provider.GetWrapUpDuration(ServerId, $"acme-eu-q-{i}").Should().Be(TimeSpan.FromSeconds(i % 600));
        }
    }

    private static QueueWrapUpDurationProvider ProviderHolding(params (string TenantId, string Queue, int Seconds)[] queues)
    {
        var provider = new QueueWrapUpDurationProvider(NullLogger<QueueWrapUpDurationProvider>.Instance);
        provider.Replace(Values(queues));
        return provider;
    }

    private static Dictionary<string, TimeSpan> Values(params (string TenantId, string Queue, int Seconds)[] queues)
        => queues.ToDictionary(
            q => QueueWrapUpDurationProvider.KeyOf(new TenantId(q.TenantId), q.Queue),
            q => TimeSpan.FromSeconds(q.Seconds));

    private sealed class CapturingLogger : ILogger<QueueWrapUpDurationProvider>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
