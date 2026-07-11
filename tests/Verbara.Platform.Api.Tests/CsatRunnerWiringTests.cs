using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;
using Verbara.Sdk.Pro.CsatRunner.Engine;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-runner Phase E2 (task 5b.6) — proves the integration keystone: (1) the composition root
/// resolves Pro's <see cref="CsatRunnerOrchestrator"/> plus all 5 seam implementations with no
/// missing dependency (a missing seam registration fails HERE, at DI resolution, not at runtime),
/// and (2) an end-to-end path — a conversation-end signal pushed by Platform's
/// <see cref="CsatConversationEndSource"/> is routed by Pro's orchestrator to the correct channel
/// adapter, which calls the correct Platform service through the seam.
/// </summary>
public sealed class CsatRunnerWiringTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;

    public CsatRunnerWiringTests(UnifiedPlatformApiFactory factory) => _factory = factory;

    // ─── DI resolution: orchestrator + all 5 seams ───────────────────────────────

    [Fact]
    public void ServiceProvider_ShouldResolveOrchestratorAndAllFiveSeams_WhenComposed()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The 5 Pro-owned seams Platform implements (the 4 Phase-E2 seams + the Phase-E template provider).
        Assert.NotNull(sp.GetRequiredService<ICsatConversationSignal>());
        Assert.NotNull(sp.GetRequiredService<ICsatEmailDispatcher>());
        Assert.NotNull(sp.GetRequiredService<ICsatSmsDispatcher>());
        Assert.NotNull(sp.GetRequiredService<ICsatConversationEndSource>());
        Assert.NotNull(sp.GetRequiredService<ICsatTemplateProvider>());

        // The Pro orchestrator itself — resolving it forces DI to construct its full graph
        // (all 3 channel adapters + their seam dependencies + sampler + options + metrics), so a
        // missing seam registration would throw here rather than at first signal.
        var orchestrator = sp.GetRequiredService<CsatRunnerOrchestrator>();
        Assert.NotNull(orchestrator);

        // All 3 Pro channel adapters resolve (webchat/email/sms), keyed by Channel downstream.
        var adapters = sp.GetServices<ICsatChannelAdapter>().ToList();
        Assert.Contains(adapters, a => a.Channel == "webchat");
        Assert.Contains(adapters, a => a.Channel == "email");
        Assert.Contains(adapters, a => a.Channel == "sms");
    }

    [Fact]
    public void ConversationEndSource_ShouldBeRegisteredAsHostedServiceAndSeam_WhenComposed()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The end-source singleton backs both the Pro seam and (in the real host) a hosted service.
        var asSeam = sp.GetRequiredService<ICsatConversationEndSource>();
        var asConcrete = sp.GetRequiredService<CsatConversationEndSource>();
        Assert.Same(asConcrete, asSeam);
    }

    // ─── End-to-end: signal → orchestrator → adapter → Platform service ──────────

    [Fact]
    public async Task ConversationEndSignal_ShouldRouteToWebchatAdapter_AndCallConversationService()
    {
        var conversationService = Substitute.For<IConversationService>();
        conversationService
            .SendMessageAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
                Arg.Any<EntityId>(), Arg.Any<ConversationOwnerKind>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message>(null!));

        // The orchestrator gates on Pro's ILicenseGuard (its own cached decision over the license
        // validation stack — the ILicenseStatus substitute AddAllProFeaturesLicensed installs is not
        // enough to flip it to Allowed in the test host). Substitute the guard directly to Allowed so
        // the routing reaches the adapter; this test proves ROUTING, not the license gate itself
        // (that gate is covered by the endpoint LicenseGateTests + the orchestrator's own Pro tests).
        var licenseGuard = Substitute.For<ILicenseGuard>();
        licenseGuard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>())
            .Returns(new LicenseGuardResult(true, null));

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IConversationService)).ToList())
                    services.Remove(d);
                services.AddSingleton(conversationService);

                foreach (var d in services.Where(d => d.ServiceType == typeof(ILicenseGuard)).ToList())
                    services.Remove(d);
                services.AddSingleton(licenseGuard);
            }));

        // Touching Services starts the (hosted-service-stubbed) host; singletons resolve normally.
        var sp = factory.Services;

        var tenant = new TenantId(UnifiedPlatformApiFactory.TestTenantId);
        var queueId = EntityId.From("queue-wiring-e2e");
        var contactId = EntityId.From("contact-wiring-e2e");
        var conversationId = EntityId.From("conv-wiring-e2e");
        var ct = CancellationToken.None;

        // Seed the stores the end-source resolves against (all registered as singletons).
        await sp.GetRequiredService<IQueueStore>().SaveAsync(new Queue
        {
            QueueId = queueId,
            TenantId = tenant,
            Name = "wiring-e2e-queue",
            // Enabled + preferred webchat + 100% sampling so the orchestrator's gates all pass.
            Csat = new CsatConfig(Enabled: true, PreferredChannel: "webchat", PromptTemplateId: null, SamplingRatePercent: 100),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        await sp.GetRequiredService<ISurveyStore>().SaveAsync(new Survey
        {
            SurveyId = EntityId.From("srv-wiring-e2e"),
            TenantId = tenant,
            Name = "Customer Satisfaction",
            Type = SurveyType.Csat,
            Questions = [],
        }, ct);

        await sp.GetRequiredService<IContactStore>().SaveAsync(new Contact
        {
            ContactId = contactId,
            TenantId = tenant,
            PreferredLanguage = "en-US",
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        await sp.GetRequiredService<IConversationStore>().SaveAsync(new Conversation
        {
            ConversationId = conversationId,
            TenantId = tenant,
            ContactId = contactId,
            Channel = ChannelType.WebChat,
            State = ConversationState.Closed,
            Owner = ConversationOwner.ForQueue(queueId),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        // Subscribe the orchestrator to the end-source (production does this in its hosted
        // ExecuteAsync; hosted services are stubbed out in the test host, so start it explicitly).
        var orchestrator = sp.GetRequiredService<CsatRunnerOrchestrator>();
        var endSource = sp.GetRequiredService<CsatConversationEndSource>();
        // The orchestrator subscribes to endSource.Ended synchronously inside its ExecuteAsync, which
        // StartAsync kicks off; a brief settle avoids racing the very first push.
        await orchestrator.StartAsync(ct);
        try
        {
            await Task.Delay(100, ct);

            // Drive the conversation-end resolution → pushes a signal onto Ended → orchestrator routes.
            await endSource.HandleClosedAsync(
                new ConversationStateChangedEvent(
                    UnifiedPlatformApiFactory.TestTenantId, conversationId.Value,
                    nameof(ConversationState.WrapUp), nameof(ConversationState.Closed)),
                ct);

            // Routing is fire-and-forget inside the orchestrator; poll for the seam call.
            var called = await WaitUntilAsync(
                () => conversationService.ReceivedCalls().Any(),
                TimeSpan.FromSeconds(10));
            Assert.True(called, "expected the webchat adapter to call IConversationService.SendMessageAsync");

            await conversationService.Received(1).SendMessageAsync(
                Arg.Is<EntityId>(id => id.Value == conversationId.Value),
                Arg.Is<TenantId>(t => t.Value == UnifiedPlatformApiFactory.TestTenantId),
                Arg.Is<MessageEnvelope>(e =>
                    e.Blocks.Count == 1 &&
                    e.Blocks[0] is TextBlock &&
                    ((TextBlock)e.Blocks[0]).Text == "csat_requested"),
                Arg.Any<EntityId>(),
                ConversationOwnerKind.System,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await orchestrator.StopAsync(ct);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(25);
        }
        return predicate();
    }
}
