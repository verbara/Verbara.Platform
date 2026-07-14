using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.CsatRunner.Adapters.Voice;
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

        // All 4 Pro channel adapters resolve (webchat/email/sms/voice), keyed by Channel downstream.
        // The voice adapter (csat-completion) resolves the voice seams + IAmiConnection; it is included
        // in the orchestrator's IEnumerable<ICsatChannelAdapter>, so a broken voice registration would
        // fault the orchestrator resolution above.
        var adapters = sp.GetServices<ICsatChannelAdapter>().ToList();
        Assert.Contains(adapters, a => a.Channel == "webchat");
        Assert.Contains(adapters, a => a.Channel == "email");
        Assert.Contains(adapters, a => a.Channel == "sms");
        Assert.Contains(adapters, a => a.Channel == "voice");

        // The voice-specific host seams the voice adapter/TtsPromptCache depend on.
        Assert.NotNull(sp.GetRequiredService<ICsatVoiceCaptureSink>());
        Assert.NotNull(sp.GetRequiredService<IDtmfSource>());
        Assert.NotNull(sp.GetRequiredService<IAmiConnection>());
    }

    // ─── csat-completion regression: headless / no-AMI boot must NOT crash ───────────
    //
    // The composition root registers IAmiConnection as a DeferredPrimaryAmiConnection: resolution of the
    // primary server's live connection is deferred to first USE, so the CsatRunnerOrchestrator (which
    // constructs the voice adapter during Host.StartAsync) resolves even when no telephony is configured.
    // The previous factory threw at boot, crashing every headless boot — notably the CI OpenAPI-export
    // capture (ci.yml "Export OpenAPI document"). This test boots the REAL Program.cs composition with NO
    // AMI stub (unlike the shared factory), leaving the production deferred wrapper in place over the real,
    // empty-in-tests VerbaraServerPool (GetServer("primary") is null), then resolves the orchestrator + voice
    // adapter to prove the whole voice branch — and thus Host.StartAsync — constructs without throwing.
    [Fact]
    public void OrchestratorGraph_ShouldResolveVoiceAdapterViaDeferredWrapper_WhenNoPrimaryAmiServerConfigured()
    {
        using var noAmiFactory = new NoAmiStubPlatformApiFactory();
        using var scope = noAmiFactory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The production IAmiConnection registration survived (deferred wrapper, not a stub), over the real
        // pool which has no servers added — i.e. GetServer("primary") is null.
        Assert.IsType<DeferredPrimaryAmiConnection>(sp.GetRequiredService<IAmiConnection>());

        // Resolving the orchestrator forces DI to construct every channel adapter, voice included, which
        // resolves IAmiConnection. With the deferred wrapper this must NOT throw despite the empty pool —
        // the exact resolution Host.StartAsync performs when it constructs the orchestrator BackgroundService.
        var orchestrator = sp.GetRequiredService<CsatRunnerOrchestrator>();
        Assert.NotNull(orchestrator);

        var adapters = sp.GetServices<ICsatChannelAdapter>().ToList();
        Assert.Contains(adapters, a => a.Channel == "voice");
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
            await Task.Delay(100, ct); // fence-allow: SETTLE — let the orchestrator's ExecuteAsync subscribe to endSource.Ended before the first push

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
            await Task.Delay(25); // fence-allow: LOOP-DRIVER — poll pacing for the fire-and-forget seam call
        }
        return predicate();
    }
}

/// <summary>
/// Test host that composes the REAL Program.cs graph (auth + in-memory stores + all-features license, like
/// <see cref="UnifiedPlatformApiFactory"/>) but — crucially — leaves the production
/// <see cref="DeferredPrimaryAmiConnection"/> registration in place instead of the shared factory's
/// <see cref="IAmiConnection"/> stub. It re-registers the deferred wrapper over the real (empty-in-tests)
/// <see cref="VerbaraServerPool"/> AFTER <c>StubVerbaraHostedServices</c> runs (same builder → last wins), so
/// the csat-completion no-AMI-boot regression exercises the real fail-at-use wrapper, not a mock.
/// </summary>
internal sealed class NoAmiStubPlatformApiFactory : WebApplicationFactory<Program>
{
    private const string TestApiKey = "no-ami-test-key-99999";
    private const string TestTenantId = "tenant-no-ami-001";
    private const string TestUserId = "no-ami-test-admin-user";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            AuthenticatedPlatformApiFactory.SetupTestAuth(services, s_hashedKey, TestTenantId, TestUserId);

            // Removes the real AMI/ARI hosted services + stubs IAmiConnection/IVerbaraServer. We keep the
            // hosted-service removal (no real AMI connect at boot) but OVERRIDE its IAmiConnection stub below.
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            // Restore the production IAmiConnection wiring: the deferred wrapper over the real pool (which
            // has no servers added in tests → GetServer("primary") is null, the exact CI no-AMI state).
            // Added LAST at the IHostBuilder level → wins over StubVerbaraHostedServices' stub.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IAmiConnection)).ToList())
                services.Remove(d);
            services.AddSingleton<IAmiConnection>(sp =>
                new DeferredPrimaryAmiConnection(sp.GetRequiredService<VerbaraServerPool>()));
        });

        var host = base.CreateHost(builder);

        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, TestTenantId);

        return host;
    }

    private static string HashKey(string rawKey)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey)));
}
