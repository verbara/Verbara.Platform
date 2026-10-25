using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Dialer.Campaign;
using Verbara.Sdk.Pro.Dialer.Models;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// dialer-honours-dnc-callbacks-and-agent-dispositions: the typify endpoint and the dialer. A voice Conversation's
/// typification is recorded without moving it, once (design D15); its disposition is resolved before the write,
/// stored with the submission and handed to Pro's <c>ApplyAgentDispositionAsync</c> seam (D5, D16); the response's
/// <c>dialerDisposition</c> reports the hand-off (owner Q8). The seam is <see cref="InMemoryCampaignStore"/>'s
/// programmable fake.
/// </summary>
public sealed partial class TypifyEndpointTests
{
    private const long CampaignId = 4242L;
    private const string ActionId = "5f0c2d9e8a7b4c3d9e1f0a2b3c4d5e6f";
    private const long DialerContactId = 4711L;

    private const string SaleLeafId = "leaf-sale";
    private const string NotHomeLeafId = "leaf-not-home";
    private const string WrongLeafId = "leaf-wrong";
    private const string NoCodeLeafId = "leaf-no-code";
    private const string NotHomeCode = "NOT-HOME";
    private const string WrongCode = "WRONG";

    private InMemoryCampaignStore Campaigns => _factory.Services.GetRequiredService<InMemoryCampaignStore>();

    // ─── Seeding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A published tenant schema with four leaves under one root: dialer code <c>SALE</c> requesting a callback,
    /// <c>NOT-HOME</c>, <c>WRONG</c> (no code of the campaign) and one without a dialer code. No required field.
    /// </summary>
    private async Task SeedDialerSchemaAsync()
    {
        TypificationNode Leaf(string id, string code, string? dialerCode, bool callback = false) => new()
        {
            NodeId = EntityId.From(id),
            ParentNodeId = EntityId.From(RootNodeId),
            Label = code,
            Code = code,
            SortOrder = 0,
            IsLeaf = true,
            Leaf = new LeafOutcome
            {
                Category = TypificationCategory.Success,
                DialerCode = dialerCode,
                TriggerCallback = callback,
                IsActive = true,
            },
        };

        var schemaId = EntityId.New();
        var schema = new TypificationSchema
        {
            SchemaId = schemaId,
            TenantId = s_tenantId,
            Name = "Dialer hand-off",
            Version = 1,
            IsPublished = true,
            MaxDepth = 5,
            Nodes =
            [
                new TypificationNode
                {
                    NodeId = EntityId.From(RootNodeId),
                    ParentNodeId = null,
                    Label = "Outcome",
                    Code = RootCode,
                    SortOrder = 0,
                    IsLeaf = false,
                },
                Leaf(SaleLeafId, "SALE_LEAF", DialerCodeValue, callback: true),
                Leaf(NotHomeLeafId, "NOT_HOME_LEAF", NotHomeCode),
                Leaf(WrongLeafId, "WRONG_LEAF", WrongCode),
                Leaf(NoCodeLeafId, "NO_CODE_LEAF", null),
            ],
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = false,
                SentimentGating = false,
                EntityFieldMap = new Dictionary<string, string>(),
            },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>().SaveAsync(schema, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>().SaveAsync(
            new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 10,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
    }

    /// <summary>Campaign <see cref="CampaignId"/>'s active codes <c>SALE</c> and <c>NOT-HOME</c>; returns their ids.</summary>
    private async Task<(long Sale, long NotHome)> SeedCampaignCodesAsync()
    {
        var store = _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>();
        async Task<long> Create(string code) => (await store.CreateAsync(
            AuthenticatedPlatformApiFactory.TestTenantId,
            new DispositionCode
            {
                TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
                CampaignId = CampaignId,
                Code = code,
                Label = code,
                IsActive = true,
            },
            CancellationToken.None)).Id;
        return (await Create(DialerCodeValue), await Create(NotHomeCode));
    }

    private Task<Conversation> SeedVoiceCampaignConversationAsync(
        ConversationState state, bool withCampaign = true, bool withActionId = true) =>
        SeedConversationAsync(state, ChannelType.Voice, CampaignMetadata(withCampaign, withActionId));

    private static Dictionary<string, string> CampaignMetadata(bool withCampaign = true, bool withActionId = true)
    {
        var metadata = new Dictionary<string, string>
        {
            ["dialerContactId"] = DialerContactId.ToString(CultureInfo.InvariantCulture),
        };
        if (withCampaign) metadata["campaignId"] = CampaignId.ToString(CultureInfo.InvariantCulture);
        if (withActionId) metadata["dialerActionId"] = ActionId;
        return metadata;
    }

    private Task<HttpResponseMessage> TypifyAsync(
        Conversation conversation, string leafId, string? notes = null, string? callbackDate = null)
    {
        var fieldValues = new Dictionary<string, string>();
        if (callbackDate is not null)
            fieldValues["callback_date"] = callbackDate;
        return _client.PostAsync(
            $"/api/conversations/{conversation.ConversationId.Value}/typify",
            JsonContent.Create(new { selectedNodePath = new[] { RootNodeId, leafId }, fieldValues, notes }));
    }

    private ConcurrentQueue<CampaignDispositionSubmittedEvent> CaptureDispositionEvents()
    {
        var events = new ConcurrentQueue<CampaignDispositionSubmittedEvent>();
        _subscriptions.Add(_factory.Services.GetRequiredService<PlatformEventBus>().Events
            .Subscribe(new DispositionEventObserver(events)));
        return events;
    }

    private async Task<Conversation> StoredConversationAsync(Conversation conversation)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetByIdAsync(s_tenantId, conversation.ConversationId, CancellationToken.None))!;
    }

    private async Task<TypificationSubmission?> StoredSubmissionAsync(Conversation conversation)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>()
            .GetByConversationIdAsync(s_tenantId, conversation.ConversationId, CancellationToken.None);
    }

    private DialerDispositionDispatch? StoredDispatch(Conversation conversation) =>
        _factory.Services.GetRequiredService<InMemoryDialerDispositionDispatchStore>()
            .Get(s_tenantId.Value, conversation.ConversationId.Value);

    private async Task<AiSuggestionRecord> SeedSuggestionAsync(Conversation conversation, string suggestedLeafId)
    {
        var record = new AiSuggestionRecord
        {
            Id = EntityId.New(),
            TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
            ConversationId = conversation.ConversationId,
            SchemaId = EntityId.New(),
            SchemaVersion = 1,
            SuggestedLeafNodeId = EntityId.From(suggestedLeafId),
            SuggestedNodePath = [RootNodeId, suggestedLeafId],
            SuggestedFieldValues = new Dictionary<string, string>(),
            Confidence = 0.92,
            ModelId = "gpt-4o-mini",
            PromptVersion = "v1",
            SurfacedBand = TypificationBand.Suggest,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>().SaveAsync(record, CancellationToken.None);
        return record;
    }

    private async Task<AiSuggestionRecord> StoredSuggestionAsync(Conversation conversation)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>().GetLatestForConversationAsync(
            EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId), conversation.ConversationId, CancellationToken.None))!;
    }

    private static async Task<JsonNode> BodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private IEnumerable<CapturedLogRecordView> Warnings =>
        _factory.CapturedLogs.Records.Where(r => r.Level == LogLevel.Warning).Select(r => new CapturedLogRecordView(r.Message));

    // ─── B.5: the seam call and the event rule ───────────────────────────────

    [Fact]
    public async Task Typify_ShouldHandTheLeafCallbackRequestToTheSeam_WhenTheLeafTriggersACallback()
    {
        await SeedDialerSchemaAsync();
        var (saleId, _) = await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, SaleLeafId, notes: "Renewed for 12 months", callbackDate: "2026-10-27T15:00:00Z");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var call = Campaigns.AgentDispositionCalls.Should().ContainSingle().Subject;
        call.TenantId.Should().Be(AuthenticatedPlatformApiFactory.TestTenantId);
        call.CampaignId.Should().Be(CampaignId);
        call.ActionId.Should().Be(ActionId);
        call.DialerContactId.Should().Be(DialerContactId);
        call.DispositionCodeId.Should().Be(saleId);
        call.Comment.Should().Be("Renewed for 12 months");
        call.CallbackAt.Should().Be(DateTimeOffset.Parse("2026-10-27T15:00:00Z", CultureInfo.InvariantCulture));
        call.RequestsCallback.Should().BeTrue("the SALE leaf requests a callback");
        call.AgentId.Should().Be(AuthenticatedPlatformApiFactory.TestUserId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Typify_ShouldNotCallTheSeamNorPublish_WhenTheConversationHasNoCampaignOrNoActionId(
        bool withCampaign, bool withActionId)
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp, withCampaign, withActionId);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Campaigns.AgentDispositionCalls.Should().BeEmpty();
        events.Should().BeEmpty();
        StoredDispatch(conv).Should().BeNull();
    }

    [Fact]
    public async Task Typify_ShouldNotCallTheSeamNorPublish_WhenTheLeafCodeMatchesNoCampaignCode()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, WrongLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Campaigns.AgentDispositionCalls.Should().BeEmpty();
        events.Should().BeEmpty("no empty-code event any more");
        Warnings.Should().Contain(w => w.Message.Contains(WrongCode, StringComparison.Ordinal)
                                       && w.Message.Contains(CampaignId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        (await StoredSubmissionAsync(conv)).Should().NotBeNull("the typification itself succeeds");
    }

    // ─── B.5: the voice state (design D15) ───────────────────────────────────

    [Fact]
    public async Task Typify_ShouldAcceptAVoiceConversationInWrapUp_WhenTheCallHasEnded()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Campaigns.AgentDispositionCalls.Should().ContainSingle();
        (await StoredConversationAsync(conv)).State.Should().Be(ConversationState.WrapUp);
    }

    [Fact]
    public async Task Typify_ShouldLeaveAnActiveVoiceConversationActive_WhenTypifiedDuringTheCall()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.Active);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredConversationAsync(conv)).State.Should().Be(ConversationState.Active,
            "the voice bridge moves the Conversation to WrapUp at the hangup, not the typification");
    }

    [Fact]
    public async Task Typify_ShouldReturn409AndKeepTheFirstSubmission_WhenTheVoiceConversationWasAlreadyTypified()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var first = await TypifyAsync(conv, SaleLeafId);
        var second = await TypifyAsync(conv, NotHomeLeafId);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(second))["error"]!.GetValue<string>().Should().Be("conversation-not-closable");
        Campaigns.AgentDispositionCalls.Should().ContainSingle();
        events.Should().ContainSingle();
        (await StoredSubmissionAsync(conv))!.LeafNodeId.Value.Should().Be(SaleLeafId);
        (await StoredConversationAsync(conv)).State.Should().Be(ConversationState.WrapUp);
    }

    [Fact]
    public async Task Typify_ShouldReturn409_WhenTheVoiceConversationIsClosed()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.Closed);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(response))["error"]!.GetValue<string>().Should().Be("conversation-not-closable");
        (await StoredSubmissionAsync(conv)).Should().BeNull();
        Campaigns.AgentDispositionCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Typify_ShouldTransitionToWrapUp_WhenADigitalConversationNoAgentHoldsIsActive()
    {
        await SeedDialerSchemaAsync();
        var conv = await SeedConversationAsync(ConversationState.Active, ChannelType.WebChat);
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            var unowned = (await store.GetByIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None))!;
            unowned.Owner = null;
            await store.SaveAsync(unowned, CancellationToken.None);
        }

        var response = await TypifyAsync(conv, NoCodeLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredConversationAsync(conv)).State.Should().Be(ConversationState.WrapUp);
    }

    [Fact]
    public async Task Typify_ShouldStoreOneAndRefuseTheOther_WhenTwoTypificationsOfAVoiceConversationRaceTogether()
    {
        await SeedDialerSchemaAsync();
        var (saleId, notHomeId) = await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);
        await SeedSuggestionAsync(conv, SaleLeafId);
        var metrics = _factory.Services.GetRequiredService<TypificationAiMetrics>();
        using var accepted = new InstrumentCounter(metrics.SuggestionAccepted);
        using var overridden = new InstrumentCounter(metrics.SuggestionOverridden);

        var responses = await Task.WhenAll(TypifyAsync(conv, SaleLeafId), TypifyAsync(conv, NotHomeLeafId));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var winner = responses[0].StatusCode == HttpStatusCode.OK ? SaleLeafId : NotHomeLeafId;
        (await StoredSubmissionAsync(conv))!.LeafNodeId.Value.Should().Be(winner);
        Campaigns.AgentDispositionCalls.Should().ContainSingle()
            .Which.DispositionCodeId.Should().Be(winner == SaleLeafId ? saleId : notHomeId);
        events.Should().ContainSingle();
        var suggestion = await StoredSuggestionAsync(conv);
        suggestion.CommittedLeafNodeId!.Value.Value.Should().Be(winner);
        suggestion.Accepted.Should().Be(winner == SaleLeafId);
        (accepted.Total + overridden.Total).Should().Be(1, "only the stored submission reconciles the suggestion");
    }

    [Fact]
    public async Task Typify_ShouldLeaveTheSuggestionAsTheFirstSubmissionReconciledIt_WhenASecondVoiceTypificationIsRefused()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);
        await SeedSuggestionAsync(conv, SaleLeafId);
        var metrics = _factory.Services.GetRequiredService<TypificationAiMetrics>();
        using var accepted = new InstrumentCounter(metrics.SuggestionAccepted);
        using var overridden = new InstrumentCounter(metrics.SuggestionOverridden);

        (await TypifyAsync(conv, SaleLeafId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TypifyAsync(conv, NotHomeLeafId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var suggestion = await StoredSuggestionAsync(conv);
        suggestion.CommittedLeafNodeId!.Value.Value.Should().Be(SaleLeafId);
        suggestion.Accepted.Should().BeTrue();
        accepted.Total.Should().Be(1);
        overridden.Total.Should().Be(0, "a refused typification counts nothing");
    }

    // ─── B.11: the response reports the hand-off (owner Q8) ──────────────────

    [Fact]
    public async Task Typify_ShouldReportApplied_WhenTheDialerAppliesTheDisposition()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("Applied");
        events.Should().ContainSingle().Which.DispositionCode.Should().Be(DialerCodeValue);
    }

    [Fact]
    public async Task Typify_ShouldReportAlreadyApplied_WhenTheDialerAcceptedADispositionForTheCallBefore()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        Campaigns.ScriptAgentDisposition(0, AgentDispositionResult.AlreadyApplied);
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, NotHomeLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("AlreadyApplied");
        Campaigns.AgentDispositionEffects.Should().Be(0);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Typify_ShouldReportAwaitingOutcome_WhenTheDialerHasNotRecordedTheCall()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        Campaigns.ScriptAgentDisposition(0, AgentDispositionResult.AwaitingOutcome);
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.Active);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("AwaitingOutcome");
        events.Should().ContainSingle();
    }

    [Fact]
    public async Task Typify_ShouldReportUnknownCall_WhenTheDialerKnowsNoCallForTheActionId()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        Campaigns.ScriptAgentDisposition(0, AgentDispositionResult.UnknownCall);
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("UnknownCall");
        events.Should().BeEmpty();
        Warnings.Should().Contain(w => w.Message.Contains(conv.ConversationId.Value, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no-campaign")]
    [InlineData("no-matching-code")]
    public async Task Typify_ShouldReportNone_WhenNothingIsHandedToTheDialer(string reason)
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp, withCampaign: reason != "no-campaign");

        var response = await TypifyAsync(conv, reason == "no-matching-code" ? WrongLeafId : SaleLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("None");
        Campaigns.AgentDispositionCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Typify_ShouldNotStoreTheDialerDisposition_WhenTheResponseReportsIt()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, SaleLeafId);

        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("Applied");
        (await StoredSubmissionAsync(conv))!.DialerDisposition.Should().BeNull();
    }

    [Fact]
    public async Task Typify_ShouldReturn200AndLeaveAPendingDispatch_WhenTheSeamThrows()
    {
        await SeedDialerSchemaAsync();
        var (_, notHomeId) = await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        Campaigns.ScriptAgentDisposition(1);
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);

        var response = await TypifyAsync(conv, NotHomeLeafId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await BodyAsync(response))["dialerDisposition"]!.GetValue<string>().Should().Be("Pending");
        (await StoredSubmissionAsync(conv)).Should().NotBeNull();
        var row = StoredDispatch(conv);
        row.Should().NotBeNull();
        row!.Result.Should().BeNull();
        row.DispositionCodeId.Should().Be(notHomeId);
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task Typify_ShouldApplyThePendingDispatchOnce_WhenTheWorkerRunsAfterAFailedHandoff()
    {
        await SeedDialerSchemaAsync();
        await SeedCampaignCodesAsync();
        var events = CaptureDispositionEvents();
        Campaigns.ScriptAgentDisposition(1);
        var conv = await SeedVoiceCampaignConversationAsync(ConversationState.WrapUp);
        (await TypifyAsync(conv, NotHomeLeafId)).StatusCode.Should().Be(HttpStatusCode.OK);
        var clock = _factory.Services.GetRequiredService<IClock>();
        var worker = new DialerDispositionDispatchWorker(
            new AdvancedClockProvider(_factory.Services, clock.UtcNow.AddMinutes(10)),
            _factory.Services.GetRequiredService<ILogger<DialerDispositionDispatchWorker>>());

        await worker.ProcessOnceAsync(CancellationToken.None);

        Campaigns.AgentDispositionCalls.Should().HaveCount(2, "the request's failed call and the worker's");
        Campaigns.AgentDispositionEffects.Should().Be(1);
        StoredDispatch(conv)!.Result.Should().Be("Applied");
        events.Should().ContainSingle();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private readonly List<IDisposable> _subscriptions = [];

    private sealed record CapturedLogRecordView(string Message);

    private sealed class DispositionEventObserver(ConcurrentQueue<CampaignDispositionSubmittedEvent> sink) : IObserver<PlatformEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PlatformEvent value)
        {
            if (value is CampaignDispositionSubmittedEvent e)
                sink.Enqueue(e);
        }
    }

    /// <summary>Sums the measurements of one instrument object (so a parallel test's host, with its own meter, is not counted).</summary>
    internal sealed class InstrumentCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public InstrumentCounter(Instrument target)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, target))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref _total, measurement));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>The host's services, with an <see cref="IClock"/> standing at <paramref name="now"/>.</summary>
    private sealed class AdvancedClockProvider(IServiceProvider inner, DateTimeOffset now) : IServiceProvider
    {
        private readonly FixedClock _clock = new(now);

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IClock) ? _clock : inner.GetService(serviceType);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
