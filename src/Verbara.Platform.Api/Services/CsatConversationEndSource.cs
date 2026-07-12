using System.Reactive.Linq;
using System.Reactive.Subjects;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform's in-process implementation of the Pro-defined
/// <see cref="ICsatConversationEndSource"/> trigger seam (csat-runner Phase E2, task 5b.4;
/// Platform/ADR-0020 + verbara-meta/ADR-0005 open-core boundary). Pro defines the trigger; Platform
/// owns the digital conversation lifecycle and the per-queue CSAT configuration and pushes a
/// <see cref="CsatConversationEndedSignal"/> onto the hot <see cref="Ended"/> stream each time a
/// digital conversation (webchat/email/sms) on a CSAT-configured queue ends. Pro's
/// <c>CsatRunnerOrchestrator</c> subscribes to <see cref="Ended"/> for the process lifetime and
/// applies the license, queue-enabled, sampling, and preferred-channel gates on each signal — it
/// never calls back into Platform.
/// </summary>
/// <remarks>
/// <para>
/// The conversation-end hook is the existing <see cref="ConversationStateChangedEvent"/> published on
/// the in-process <see cref="PlatformEventBus"/> (there is no dedicated "conversation ended" event;
/// the terminal <see cref="ConversationState.Closed"/> transition is the canonical end signal, the
/// same event <c>PushToHubRelay</c> and <c>RealtimeStateBridge</c> already consume). This service is a
/// <see cref="BackgroundService"/> that subscribes to
/// <c>PlatformEventBus.Events.OfType&lt;ConversationStateChangedEvent&gt;()</c> and, for each
/// transition into <see cref="ConversationState.Closed"/>, resolves the queue-config snapshot +
/// identity/recipient/locale and pushes a signal — mirroring the
/// <c>VerbaraCapacitySyncService</c> subscription pattern.
/// </para>
/// <para>
/// Resolution: the <see cref="Conversation"/> (channel + queue owner + contact) is loaded via
/// <see cref="IConversationStore"/>; only the digital channels
/// (<see cref="ChannelType.WebChat"/>/<see cref="ChannelType.Email"/>/<see cref="ChannelType.Sms"/>)
/// proceed. The queue's <see cref="CsatConfig"/> snapshot
/// (<c>Enabled</c>/<c>PreferredChannel</c>/<c>SamplingRatePercent</c>) comes from
/// <see cref="IQueueStore"/>; the tenant's active CSAT <see cref="Survey"/> yields the
/// <c>SurveyId</c> (<c>QuestionId</c> = <see cref="SurveyQuestionIds.CsatRating"/>); the
/// <see cref="Contact"/>'s channel address is the recipient (null for webchat) and its
/// <see cref="Contact.PreferredLanguage"/> the locale (default <c>en-US</c>). A fresh
/// <c>SurveyResponseId</c> is minted per signal. A conversation whose queue has no CSAT config (or
/// CSAT disabled) is still pushed with <c>CsatEnabled=false</c> so the orchestrator's
/// queue-disabled skip path owns the decision (single source of truth).
/// </para>
/// </remarks>
internal sealed partial class CsatConversationEndSource : BackgroundService, ICsatConversationEndSource
{
    private const string DefaultLocale = "en-US";

    private readonly PlatformEventBus _eventBus;
    private readonly IConversationStore _conversationStore;
    private readonly IQueueStore _queueStore;
    private readonly ISurveyStore _surveyStore;
    private readonly IContactStore _contactStore;
    private readonly ILogger<CsatConversationEndSource> _logger;

    private readonly Subject<CsatConversationEndedSignal> _ended = new();
    private IDisposable? _subscription;

    public CsatConversationEndSource(
        PlatformEventBus eventBus,
        IConversationStore conversationStore,
        IQueueStore queueStore,
        ISurveyStore surveyStore,
        IContactStore contactStore,
        ILogger<CsatConversationEndSource> logger)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(queueStore);
        ArgumentNullException.ThrowIfNull(surveyStore);
        ArgumentNullException.ThrowIfNull(contactStore);
        ArgumentNullException.ThrowIfNull(logger);
        _eventBus = eventBus;
        _conversationStore = conversationStore;
        _queueStore = queueStore;
        _surveyStore = surveyStore;
        _contactStore = contactStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public IObservable<CsatConversationEndedSignal> Ended => _ended;

    /// <summary>Exposed for testing — true once the end-event subscription is attached.</summary>
    internal bool IsSubscribed => Volatile.Read(ref _subscription) is not null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _subscription = _eventBus.Events
            .OfType<ConversationStateChangedEvent>()
            .Where(static e => string.Equals(e.NewState, nameof(ConversationState.Closed), StringComparison.Ordinal))
            .Subscribe(
                onNext: evt => HandleClosedSafely(evt, stoppingToken),
                onError: HandleSubscriptionFault,
                onCompleted: () => { /* normal completion — host shutting down */ });

        stoppingToken.Register(() => _subscription?.Dispose());

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void HandleClosedSafely(ConversationStateChangedEvent evt, CancellationToken ct)
    {
        // Fire-and-forget: the Rx subscription is synchronous, but signal resolution is async.
        // Failures are swallowed (logged) so one bad conversation never faults the whole stream.
        _ = HandleClosedAsync(evt, ct);
    }

    private void HandleSubscriptionFault(Exception ex)
    {
        LogSubscriptionFault(ex.Message);
        Interlocked.Exchange(ref _subscription, null);
    }

    /// <summary>
    /// Resolves and pushes the ended-conversation signal. Exposed internally so tests can drive the
    /// resolution + push without the Rx plumbing. Returns without pushing (and without throwing) when
    /// the conversation is missing or on a non-digital channel.
    /// </summary>
    internal async Task HandleClosedAsync(ConversationStateChangedEvent evt, CancellationToken ct)
    {
        try
        {
            var tenant = new TenantId(evt.TenantId);
            if (!EntityId.IsValid(evt.ConversationId))
                return;

            var conversation = await _conversationStore
                .GetByIdAsync(tenant, EntityId.From(evt.ConversationId), ct).ConfigureAwait(false);
            if (conversation is null)
                return;

            var nativeChannel = MapChannel(conversation.Channel);
            if (nativeChannel is null)
                return; // non-digital (voice/whatsapp/etc.) — the engine handles only email/sms/webchat

            // Queue-config snapshot (Enabled / PreferredChannel / SamplingRatePercent).
            string queueName = string.Empty;
            var csatEnabled = false;
            string? preferredChannel = null;
            int? samplingRatePercent = null;
            if (conversation.Owner is { Kind: ConversationOwnerKind.Queue, OwnerId: { } queueId })
            {
                var queue = await _queueStore.GetByIdAsync(tenant, queueId, ct).ConfigureAwait(false);
                if (queue is not null)
                {
                    queueName = queue.Name;
                    if (queue.Csat is { } csat)
                    {
                        csatEnabled = csat.Enabled;
                        preferredChannel = csat.PreferredChannel;
                        samplingRatePercent = csat.SamplingRatePercent;
                    }
                }
            }

            // Active CSAT survey for the tenant → SurveyId; QuestionId is the well-known constant.
            var surveys = await _surveyStore.GetActiveAsync(tenant, ct).ConfigureAwait(false);
            var csatSurvey = surveys.FirstOrDefault(s => s.Type == SurveyType.Csat);
            if (csatSurvey is null)
                return; // no CSAT survey configured for the tenant — nothing to solicit against

            // Recipient address (null for webchat, which addresses the open conversation) + locale.
            string? recipientAddress = null;
            var locale = DefaultLocale;
            var contact = await _contactStore
                .GetByIdAsync(tenant, conversation.ContactId, ct).ConfigureAwait(false);
            if (contact is not null)
            {
                if (!string.IsNullOrWhiteSpace(contact.PreferredLanguage))
                    locale = contact.PreferredLanguage!;
                if (conversation.Channel is not ChannelType.WebChat)
                    recipientAddress = contact.FindAddress(conversation.Channel)?.Address;
            }

            var signal = new CsatConversationEndedSignal
            {
                TenantId = evt.TenantId,
                ConversationId = evt.ConversationId,
                QueueName = queueName,
                NativeChannel = nativeChannel,
                Locale = locale,
                SurveyId = csatSurvey.SurveyId.Value,
                QuestionId = SurveyQuestionIds.CsatRating,
                SurveyResponseId = EntityId.New().Value,
                CsatEnabled = csatEnabled,
                PreferredChannel = preferredChannel,
                SamplingRatePercent = samplingRatePercent,
                RecipientAddress = recipientAddress,
            };

            _ended.OnNext(signal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResolutionError(ex, evt.ConversationId);
        }
    }

    // Digital channels the CSAT engine routes over; every other ChannelType is out of scope.
    private static string? MapChannel(ChannelType channel) => channel switch
    {
        ChannelType.WebChat => "webchat",
        ChannelType.Email => "email",
        ChannelType.Sms => "sms",
        _ => null,
    };

    public override void Dispose()
    {
        _subscription?.Dispose();
        _ended.Dispose();
        base.Dispose();
    }

    [LoggerMessage(1, LogLevel.Critical,
        "[CSAT] Conversation-end trigger subscription faulted — orchestrator will stop receiving signals. Reason: {Reason}")]
    private partial void LogSubscriptionFault(string reason);

    [LoggerMessage(2, LogLevel.Error,
        "[CSAT] Failed to resolve conversation-end signal for conversation {ConversationId}")]
    private partial void LogResolutionError(Exception ex, string conversationId);
}
