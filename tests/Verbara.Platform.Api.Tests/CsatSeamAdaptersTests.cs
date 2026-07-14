using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-runner Phase E2 (task 5b.6) — unit coverage that each Platform seam adapter bridges its Pro
/// contract onto the correct Platform service, using NSubstitute fakes of the Platform interfaces.
/// The end-to-end DI + routing wiring is covered by <see cref="CsatRunnerWiringTests"/>.
/// </summary>
public sealed class CsatSeamAdaptersTests
{
    // ─── 5b.1 CsatConversationSignalAdapter ──────────────────────────────────────

    [Fact]
    public async Task SendSystemMessageAsync_ShouldSendSystemDirectedTextBlock_WhenInvoked()
    {
        var conversationService = Substitute.For<IConversationService>();
        conversationService
            .SendMessageAsync(
                Arg.Any<EntityId>(), Arg.Any<TenantId>(), Arg.Any<MessageEnvelope>(),
                Arg.Any<EntityId>(), Arg.Any<ConversationOwnerKind>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Message>(null!));

        var adapter = new CsatConversationSignalAdapter(conversationService);

        await adapter.SendSystemMessageAsync("ten-42", "conv-8f2a1c4e", "csat_requested", CancellationToken.None);

        await conversationService.Received(1).SendMessageAsync(
            Arg.Is<EntityId>(id => id.Value == "conv-8f2a1c4e"),
            Arg.Is<TenantId>(t => t.Value == "ten-42"),
            Arg.Is<MessageEnvelope>(e =>
                e.Blocks.Count == 1 &&
                e.Blocks[0] is TextBlock &&
                ((TextBlock)e.Blocks[0]).Text == "csat_requested"),
            Arg.Any<EntityId>(),
            ConversationOwnerKind.System,
            Arg.Any<CancellationToken>());
    }

    // ─── 5b.2 CsatEmailDispatcherAdapter ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldMapReplyToOntoEmailMessage_WhenDispatchingCsatEmail()
    {
        var emailService = Substitute.For<IEmailService>();
        EmailMessage? captured = null;
        await emailService.SendAsync(
            Arg.Do<EmailMessage>(m => captured = m), Arg.Any<CancellationToken>());

        var adapter = new CsatEmailDispatcherAdapter(emailService);
        var request = new CsatEmailRequest
        {
            TenantId = "ten-42",
            RecipientAddress = "customer@example.com",
            Subject = "How did we do?",
            Body = "Rate 1-5",
            ReplyToAddress = "csat+tok123@csat.verbara.local",
        };

        await adapter.SendAsync(request, CancellationToken.None);

        await emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        Assert.NotNull(captured);
        Assert.Equal("csat+tok123@csat.verbara.local", captured!.ReplyToAddress);
        Assert.Equal("How did we do?", captured.Subject);
        Assert.Equal("Rate 1-5", captured.TextBody);
        Assert.Single(captured.Recipients);
        Assert.Equal("customer@example.com", captured.Recipients[0].Email);
    }

    // ─── 5b.3 CsatSmsDispatcherAdapter ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldThrow_WhenSmsProviderNotConfigured()
    {
        var adapter = new CsatSmsDispatcherAdapter(
            Substitute.For<IConversationStore>(),
            Substitute.For<IQueueStore>(),
            Substitute.For<ISurveyStore>(),
            defaultFromNumber: "+15550001111",
            smsProvider: null,
            dataSource: null);

        var request = new CsatSmsRequest
        {
            TenantId = "ten-42",
            ConversationId = "conv-1",
            RecipientPhone = "+15557654321",
            Body = "Rate 1-5",
            CorrelationWindow = TimeSpan.FromHours(24),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_ShouldThrowWithoutSending_WhenDataSourceUnavailable()
    {
        // Guard-first: with no NpgsqlDataSource the adapter cannot record the pending-dispatch row
        // the correlator depends on, so it refuses to send an SMS it could never correlate.
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SmsSendResult(true, "msg-1", null, null)));

        var adapter = new CsatSmsDispatcherAdapter(
            Substitute.For<IConversationStore>(),
            Substitute.For<IQueueStore>(),
            Substitute.For<ISurveyStore>(),
            defaultFromNumber: "+15550001111",
            smsProvider: provider,
            dataSource: null);

        var request = new CsatSmsRequest
        {
            TenantId = "ten-42",
            ConversationId = "conv-1",
            RecipientPhone = "+15557654321",
            Body = "Rate 1-5",
            CorrelationWindow = TimeSpan.FromHours(24),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.SendAsync(request, CancellationToken.None));

        await provider.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldThrow_WhenProviderReportsSendFailure()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SmsSendResult(false, null, "21211", "invalid to")));

        // A non-null dataSource is required to reach the send path's failure branch before any INSERT.
        var adapter = new CsatSmsDispatcherAdapter(
            Substitute.For<IConversationStore>(),
            Substitute.For<IQueueStore>(),
            Substitute.For<ISurveyStore>(),
            defaultFromNumber: "+15550001111",
            smsProvider: provider,
            dataSource: Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused"));

        var request = new CsatSmsRequest
        {
            TenantId = "ten-42",
            ConversationId = "conv-1",
            RecipientPhone = "+15557654321",
            Body = "Rate 1-5",
            CorrelationWindow = TimeSpan.FromHours(24),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await adapter.SendAsync(request, CancellationToken.None));
    }

    // ─── 5b.4 CsatConversationEndSource ──────────────────────────────────────────

    [Fact]
    public async Task HandleClosedAsync_ShouldPushSignalWithQueueSnapshot_WhenDigitalConversationEnds()
    {
        var tenant = new TenantId("ten-42");
        var queueId = EntityId.From("queue-support");
        var contactId = EntityId.From("contact-1");
        var conversationId = EntityId.From("conv-web-1");

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.GetByIdAsync(tenant, conversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = conversationId,
                TenantId = tenant,
                ContactId = contactId,
                Channel = ChannelType.WebChat,
                State = ConversationState.Closed,
                Owner = ConversationOwner.ForQueue(queueId),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var queueStore = Substitute.For<IQueueStore>();
        queueStore.GetByIdAsync(tenant, queueId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Queue?>(new Queue
            {
                QueueId = queueId,
                TenantId = tenant,
                Name = "support-tier1",
                Csat = new CsatConfig(Enabled: true, PreferredChannel: "webchat", PromptTemplateId: null, SamplingRatePercent: 20),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var surveyStore = Substitute.For<ISurveyStore>();
        surveyStore.GetActiveAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Survey>>([new Survey
            {
                SurveyId = EntityId.From("srv-csat-v1"),
                TenantId = tenant,
                Name = "Customer Satisfaction",
                Type = SurveyType.Csat,
                Questions = [],
            }]));

        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(tenant, contactId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Contact?>(new Contact
            {
                ContactId = contactId,
                TenantId = tenant,
                PreferredLanguage = "es-419",
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var source = new CsatConversationEndSource(
            new PlatformEventBus(),
            conversationStore, queueStore, surveyStore, contactStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CsatConversationEndSource>.Instance);

        CsatConversationEndedSignal? pushed = null;
        using var sub = source.Ended.Subscribe(s => pushed = s);

        await source.HandleClosedAsync(
            new ConversationStateChangedEvent("ten-42", "conv-web-1", nameof(ConversationState.WrapUp), nameof(ConversationState.Closed)),
            CancellationToken.None);

        Assert.NotNull(pushed);
        Assert.Equal("ten-42", pushed!.TenantId);
        Assert.Equal("conv-web-1", pushed.ConversationId);
        Assert.Equal("support-tier1", pushed.QueueName);
        Assert.Equal("webchat", pushed.NativeChannel);
        Assert.Equal("es-419", pushed.Locale);
        Assert.Equal("srv-csat-v1", pushed.SurveyId);
        Assert.Equal(SurveyQuestionIds.CsatRating, pushed.QuestionId);
        Assert.True(pushed.CsatEnabled);
        Assert.Equal("webchat", pushed.PreferredChannel);
        Assert.Equal(20, pushed.SamplingRatePercent);
        Assert.False(string.IsNullOrEmpty(pushed.SurveyResponseId));
        Assert.Null(pushed.RecipientAddress); // webchat addresses the open conversation, no external address
    }

    [Fact]
    public async Task HandleClosedAsync_ShouldNotPush_WhenChannelIsNonDigital()
    {
        var tenant = new TenantId("ten-42");
        var conversationId = EntityId.From("conv-voice-1");

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.GetByIdAsync(tenant, conversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = conversationId,
                TenantId = tenant,
                ContactId = EntityId.From("contact-1"),
                Channel = ChannelType.Voice,
                State = ConversationState.Closed,
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var source = new CsatConversationEndSource(
            new PlatformEventBus(),
            conversationStore,
            Substitute.For<IQueueStore>(),
            Substitute.For<ISurveyStore>(),
            Substitute.For<IContactStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CsatConversationEndSource>.Instance);

        var pushed = false;
        using var sub = source.Ended.Subscribe(_ => pushed = true);

        // A voice conversation that reaches Closed is NOT a solicit event — voice solicits on WrapUp only.
        await source.HandleClosedAsync(
            new ConversationStateChangedEvent("ten-42", "conv-voice-1", nameof(ConversationState.Active), nameof(ConversationState.Closed)),
            CancellationToken.None);

        Assert.False(pushed);
    }

    // ─── csat-completion — voice channel trigger (design D1) ─────────────────────

    private static (IConversationStore, IQueueStore, ISurveyStore, IContactStore) VoiceStores(
        TenantId tenant, EntityId conversationId, EntityId queueId, EntityId contactId, ConversationState state, bool csatEnabled)
    {
        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore.GetByIdAsync(tenant, conversationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = conversationId,
                TenantId = tenant,
                ContactId = contactId,
                Channel = ChannelType.Voice,
                State = state,
                Owner = ConversationOwner.ForQueue(queueId),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var queueStore = Substitute.For<IQueueStore>();
        queueStore.GetByIdAsync(tenant, queueId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Queue?>(new Queue
            {
                QueueId = queueId,
                TenantId = tenant,
                Name = "support-tier1",
                Csat = new CsatConfig(Enabled: csatEnabled, PreferredChannel: "voice", PromptTemplateId: null, SamplingRatePercent: 100),
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        var surveyStore = Substitute.For<ISurveyStore>();
        surveyStore.GetActiveAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Survey>>([new Survey
            {
                SurveyId = EntityId.From("srv-csat-v1"),
                TenantId = tenant,
                Name = "Customer Satisfaction",
                Type = SurveyType.Csat,
                Questions = [],
            }]));

        var contactStore = Substitute.For<IContactStore>();
        contactStore.GetByIdAsync(tenant, contactId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Contact?>(new Contact
            {
                ContactId = contactId,
                TenantId = tenant,
                PreferredLanguage = "es-419",
                CreatedAt = DateTimeOffset.UtcNow,
            }));

        return (conversationStore, queueStore, surveyStore, contactStore);
    }

    [Fact]
    public async Task HandleClosedAsync_ShouldPushVoiceSignal_WhenAnsweredVoiceCallWrapsUp()
    {
        var tenant = new TenantId("ten-42");
        var conversationId = EntityId.From("conv-voice-wrap");
        var (conversationStore, queueStore, surveyStore, contactStore) = VoiceStores(
            tenant, conversationId, EntityId.From("queue-support"), EntityId.From("contact-1"),
            ConversationState.WrapUp, csatEnabled: true);

        var source = new CsatConversationEndSource(
            new PlatformEventBus(),
            conversationStore, queueStore, surveyStore, contactStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CsatConversationEndSource>.Instance);

        CsatConversationEndedSignal? pushed = null;
        using var sub = source.Ended.Subscribe(s => pushed = s);

        // Answered voice call: Active → WrapUp is the solicit transition.
        await source.HandleClosedAsync(
            new ConversationStateChangedEvent("ten-42", "conv-voice-wrap", nameof(ConversationState.Active), nameof(ConversationState.WrapUp)),
            CancellationToken.None);

        Assert.NotNull(pushed);
        Assert.Equal("voice", pushed!.NativeChannel);
        Assert.Equal("support-tier1", pushed.QueueName);
        Assert.Equal("srv-csat-v1", pushed.SurveyId);
        Assert.True(pushed.CsatEnabled);
    }

    [Fact]
    public async Task HandleClosedAsync_ShouldNotPush_WhenVoiceCallAbandoned()
    {
        var tenant = new TenantId("ten-42");
        var conversationId = EntityId.From("conv-voice-aband");
        var (conversationStore, queueStore, surveyStore, contactStore) = VoiceStores(
            tenant, conversationId, EntityId.From("queue-support"), EntityId.From("contact-1"),
            ConversationState.Abandoned, csatEnabled: true);

        var source = new CsatConversationEndSource(
            new PlatformEventBus(),
            conversationStore, queueStore, surveyStore, contactStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CsatConversationEndSource>.Instance);

        var pushed = false;
        using var sub = source.Ended.Subscribe(_ => pushed = true);

        // Never-answered voice call: Queued → Abandoned is NOT in the solicit filter, so nothing fires.
        await source.HandleClosedAsync(
            new ConversationStateChangedEvent("ten-42", "conv-voice-aband", nameof(ConversationState.Queued), nameof(ConversationState.Abandoned)),
            CancellationToken.None);

        Assert.False(pushed);
    }

    [Fact]
    public async Task HandleClosedAsync_ShouldPushWithCsatEnabledFalse_WhenVoiceQueueCsatDisabled()
    {
        var tenant = new TenantId("ten-42");
        var conversationId = EntityId.From("conv-voice-disabled");
        var (conversationStore, queueStore, surveyStore, contactStore) = VoiceStores(
            tenant, conversationId, EntityId.From("queue-support"), EntityId.From("contact-1"),
            ConversationState.WrapUp, csatEnabled: false);

        var source = new CsatConversationEndSource(
            new PlatformEventBus(),
            conversationStore, queueStore, surveyStore, contactStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CsatConversationEndSource>.Instance);

        CsatConversationEndedSignal? pushed = null;
        using var sub = source.Ended.Subscribe(s => pushed = s);

        await source.HandleClosedAsync(
            new ConversationStateChangedEvent("ten-42", "conv-voice-disabled", nameof(ConversationState.Active), nameof(ConversationState.WrapUp)),
            CancellationToken.None);

        // The signal is still pushed with CsatEnabled=false so the orchestrator's queue-disabled skip
        // path owns the decision (single source of truth).
        Assert.NotNull(pushed);
        Assert.Equal("voice", pushed!.NativeChannel);
        Assert.False(pushed.CsatEnabled);
    }
}
