using Verbara.Platform.Api.Health;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.Reports;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Impersonation;
using Verbara.Platform.Core.Reports;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Workers.Resilience;

/// <summary>
/// Smoke tests for workers whose outer fatal try-catch is not trivially injectable from
/// a unit test. These verify the resilience contract via two observable properties:
/// <list type="bullet">
///   <item>the worker constructs with substituted dependencies + a null
///         <see cref="Verbara.Sdk.Resilience.ResiliencePolicy"/> (NoOp fallback);</item>
///   <item><see cref="BackgroundService.StartAsync"/> + <see cref="BackgroundService.StopAsync"/>
///         complete cleanly when the stoppingToken is cancelled — <c>ExecuteTask</c> does NOT
///         fault on cancellation (the outer try-catch's <c>when</c> filter must swallow OCE).</item>
/// </list>
/// Triggering a non-cancellation fatal exception is verified end-to-end via the four
/// Tier-1 deep-test classes that target heartbeat-shaped or subscription-shaped failure
/// modes.
/// </summary>
public sealed class SimpleWorkerSmokeTests
{
    // ─── helpers ─────────────────────────────────────────────────────────────

    private static IClock NewClock()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        return clock;
    }

    private static async Task StartCancelStopAsync(BackgroundService worker)
    {
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        var fault = await WorkerResilienceTestHelpers.AwaitExecuteFaultAsync(
            worker, TimeSpan.FromSeconds(5));
        fault.Should().BeNull("cancellation must travel the outer when-filter, not the fatal handler");
    }

    // ─── CampaignMetricsPoller ───────────────────────────────────────────────

    [Fact]
    public async Task CampaignMetricsPoller_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var sut = new CampaignMetricsPoller(
            Substitute.For<CampaignStoreBase>(),
            new PlatformEventBus(),
            NullLogger<CampaignMetricsPoller>.Instance);

        await StartCancelStopAsync(sut);
    }

    // ─── RetentionPurgeService ───────────────────────────────────────────────

    [Fact]
    public async Task RetentionPurgeService_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var config = new ConfigurationBuilder().Build();
        var sut = new RetentionPurgeService(
            Substitute.For<ITenantRetentionPolicyStore>(),
            Substitute.For<IConversationStore>(),
            Substitute.For<IMessageStore>(),
            Substitute.For<IAuthEventStore>(),
            Substitute.For<IAuditStore>(),
            Substitute.For<IUsageRecordStore>(),
            Substitute.For<IPurgeLogStore>(),
            NewClock(),
            NullLogger<RetentionPurgeService>.Instance,
            config);

        await StartCancelStopAsync(sut);
    }

    // ─── AuditRetentionService ───────────────────────────────────────────────

    [Fact]
    public async Task AuditRetentionService_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var sut = new AuditRetentionService(
            Substitute.For<IAuditStore>(),
            NewClock(),
            NullLogger<AuditRetentionService>.Instance);

        await StartCancelStopAsync(sut);
    }

    // ─── ImpersonationSessionTimeoutService ──────────────────────────────────

    [Fact]
    public async Task ImpersonationSessionTimeoutService_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var sut = new ImpersonationSessionTimeoutService(
            Substitute.For<IImpersonationSessionStore>(),
            Substitute.For<ITenantAuthConfigStore>(),
            Substitute.For<IAuditService>(),
            NullLogger<ImpersonationSessionTimeoutService>.Instance,
            clock: TimeProvider.System,
            sweepInterval: TimeSpan.FromMilliseconds(100));

        await StartCancelStopAsync(sut);
    }

    // ─── ReportSchedulerService ──────────────────────────────────────────────

    [Fact]
    public async Task ReportSchedulerService_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var sut = new ReportSchedulerService(
            Substitute.For<IScheduledReportStore>(),
            new ReportDataBuilderRegistry(Array.Empty<IReportDataBuilder>()),
            Substitute.For<IReportRenderer>(),
            Substitute.For<IReportRenderer>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IEmailTemplateService>(),
            Substitute.For<ITenantBrandingStore>(),
            Substitute.For<IAuditService>(),
            NewClock(),
            Options.Create(new SmtpOptions()),
            NullLogger<ReportSchedulerService>.Instance);

        await StartCancelStopAsync(sut);
    }

    // ─── VerbaraCapacitySyncService ─────────────────────────────────────────

    [Fact]
    public async Task VerbaraCapacitySyncService_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        var sut = new VerbaraCapacitySyncService(
            Substitute.For<IAgentCapacityService>(),
            Substitute.For<IAgentStore>(),
            new PlatformEventBus(),
            NullLogger<VerbaraCapacitySyncService>.Instance);

        await StartCancelStopAsync(sut);
    }

    // ─── AuthWriteQueue ──────────────────────────────────────────────────────

    [Fact]
    public async Task AuthWriteQueue_ShouldSurviveCancellation_WhenStoppingTokenCancelled()
    {
        // AuthWriteQueue needs an IServiceProvider for batch processing. An empty service
        // collection is fine — the consumer only resolves dependencies inside ProcessBatchAsync,
        // which is only reached when items are enqueued (we don't enqueue any in smoke).
        var services = new ServiceCollection().BuildServiceProvider();
        var sut = new AuthWriteQueue(services, NullLogger<AuthWriteQueue>.Instance);

        await StartCancelStopAsync(sut);
        sut.Dispose();
    }
}
