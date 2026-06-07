using System.Globalization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

internal interface ICallbackOriginator
{
    /// <summary>
    /// Originates a rescue callback to <paramref name="number"/>, routing the answered customer into
    /// their origin queue (<paramref name="originQueueId"/>) at the FRONT (Asterisk QUEUE_PRIO). Creates a
    /// tracked Conversation (Queued, Voice, metadata rescuedFrom + callbackAttempts + direction=callback-rescue)
    /// ONLY after a successful Originate (no orphan on failure). Returns true iff the Originate was accepted.
    /// Caller MUST be leader-gated (the AMI single-writer contract) — the CallbackRescueWorker is.
    /// </summary>
    Task<bool> OriginateCallbackAsync(TenantId tenantId, string number, EntityId originQueueId, EntityId rescuedFrom, int callbackAttempts, CancellationToken ct);
}

/// <summary>
/// W5b voice caller-rescue originator. When an agent's voice leg dies mid-call the customer is dropped;
/// rather than re-routing a dead leg, this dials a NEW outbound call DIRECTLY to the customer's number
/// (<c>PJSIP/{trunk}/{number}</c>, unlike click-to-dial which rings the agent first) and drops the
/// answered customer back into their ORIGINAL queue at the FRONT, where the next live agent answers.
///
/// <para>Reuses the inbound <c>[stasis-queue]</c> dialplan contract
/// (<see cref="StasisInboundConsumer.StasisQueueContext"/> / <see cref="StasisInboundConsumer.QueueEntryExtension"/>
/// runs <c>Queue(${QUEUE_NAME})</c>) — no new context. Front-of-queue for VOICE is the Asterisk
/// <c>QUEUE_PRIO</c> channel variable (app_queue reads it before <c>Queue()</c>), NOT the platform
/// <c>queue_priority</c> column (that orders only the DIGITAL <c>QueueDistributionWorker</c>); the column
/// is still set to <c>-1</c> for reporting parity with W5 digital.</para>
///
/// <para>The originate is correlated to a pre-created tracked Conversation by reusing the existing
/// <c>VERBARA_OUTBOUND_ID</c> channel variable: <see cref="VoiceConversationBridge"/>'s
/// <c>OnCallStarted</c>/<c>LinkOutboundCallAsync</c> already reads it on every start leg and stamps the
/// real <c>VoiceLinkedId</c> onto the matching pre-created Conversation (for a Queue/null-owned conv it
/// just links — the agent-only screen-pop branch is skipped). The Conversation row is persisted only
/// AFTER a successful Originate so a rejected dial never leaves an orphan.</para>
///
/// <para>DNC is intentionally SKIPPED — a rescue callback returns a call to someone who just called us;
/// DNC (cold-outbound compliance) does not apply. The caller (<c>CallbackRescueWorker</c>, task A6) MUST
/// hold the AMI-owner leader lease — this type does not gate.</para>
/// </summary>
internal sealed partial class CallbackOriginator : ICallbackOriginator
{
    private const string TenantVariable = "TENANT_ID";
    private const string OutboundIdVariable = "VERBARA_OUTBOUND_ID";
    private const string QueuePrioVariable = "QUEUE_PRIO";
    private const string FrontOfQueuePriority = "10";
    private const string PrimaryNode = "primary";
    private const long OriginateTimeoutMs = 30_000;

    private readonly IConversationStore _conversations;
    private readonly IQueueStore _queues;
    private readonly ITenantStore _tenants;
    private readonly IContactIdentityResolver _contacts;
    private readonly OriginateExecutorBase _originate;
    private readonly OutboundRouteResolverBase? _routes;
    private readonly IClock _clock;
    private readonly string? _defaultTrunk;
    private readonly ILogger<CallbackOriginator> _logger;

    public CallbackOriginator(
        IConversationStore conversations,
        IQueueStore queues,
        ITenantStore tenants,
        IContactIdentityResolver contacts,
        OriginateExecutorBase originate,
        OutboundRouteResolverBase? routes,
        IClock clock,
        string? defaultTrunk,
        ILogger<CallbackOriginator> logger)
    {
        _conversations = conversations;
        _queues = queues;
        _tenants = tenants;
        _contacts = contacts;
        _originate = originate;
        _routes = routes;
        _clock = clock;
        _defaultTrunk = defaultTrunk;
        _logger = logger;
    }

    public async Task<bool> OriginateCallbackAsync(
        TenantId tenantId, string number, EntityId originQueueId, EntityId rescuedFrom, int callbackAttempts, CancellationToken ct)
    {
        var dialNumber = NormalizeNumber(number);
        if (string.IsNullOrEmpty(dialNumber))
            return false;

        // Origin queue → the [stasis-queue] QUEUE_NAME convention "{tenant}-{queue.Name}" (mirrors the
        // inbound consumer + blind transfer). The same queue the customer was dropped from.
        var queue = await _queues.GetByIdAsync(tenantId, originQueueId, ct).ConfigureAwait(false);
        if (queue is null)
        {
            LogQueueNotFound(originQueueId.Value, rescuedFrom.Value);
            return false;
        }
        var asteriskQueue = $"{tenantId.Value}-{queue.Name}";

        // Outbound route → trunk. Optional resolver (only the full dialer Postgres storage registers it);
        // fall back to the configured default trunk. No trunk → cannot dial out.
        string? trunk = null;
        if (_routes is not null)
        {
            try
            {
                trunk = (await _routes.ResolveAsync(tenantId.Value, dialNumber, campaignId: null, ct).ConfigureAwait(false))?.Name;
            }
            catch (Exception ex)
            {
                LogRouteFailed(dialNumber, ex.Message);
            }
        }
        trunk ??= _defaultTrunk;
        if (string.IsNullOrWhiteSpace(trunk))
        {
            LogNoTrunk(dialNumber, rescuedFrom.Value);
            return false;
        }

        // Caller ID: the tenant outbound setting (empty → the dialplan/trunk default is presented).
        var tenant = await _tenants.GetAsync(tenantId.Value, ct).ConfigureAwait(false);
        var callerId = tenant?.Options.OutboundCallerId ?? "";

        // The Conversation id IS the correlation id stamped on the Originate — the bridge links by id.
        // Generated up front so the channel var carries it; the row is persisted only after a successful
        // Originate so a rejected dial leaves no orphan.
        var conversationId = EntityId.New();

        var action = new OriginateAction
        {
            Channel = $"PJSIP/{trunk}/{dialNumber}",
            Context = StasisInboundConsumer.StasisQueueContext,
            Exten = StasisInboundConsumer.QueueEntryExtension,
            Priority = 1,
            IsAsync = true,
            Timeout = OriginateTimeoutMs,
        };
        if (!string.IsNullOrEmpty(callerId))
            action.CallerId = callerId;
        action.SetVariable(TenantVariable, tenantId.Value);
        action.SetVariable(StasisInboundConsumer.QueueNameVariable, asteriskQueue);
        action.SetVariable(OutboundIdVariable, conversationId.Value);
        action.SetVariable(QueuePrioVariable, FrontOfQueuePriority);

        OriginateResult result;
        try
        {
            result = await _originate.ExecuteAsync(action, PrimaryNode, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOriginateThrew(conversationId.Value, dialNumber, ex.Message);
            return false;
        }

        if (!result.Success)
        {
            LogOriginateRejected(conversationId.Value, dialNumber, result.ErrorMessage ?? "rejected");
            return false;
        }

        // Same customer as the dropped call → reuse the original conversation's ContactId. The original
        // is in WrapUp (the dead-leg flow parks it there) so it should always load; if it is gone, resolve
        // the contact by the dialed number via the inbound identity resolver.
        var contactId = await ResolveContactIdAsync(tenantId, dialNumber, rescuedFrom, ct).ConfigureAwait(false);

        var conversation = new Conversation
        {
            ConversationId = conversationId,
            TenantId = tenantId,
            ContactId = contactId,
            Channel = ChannelType.Voice,
            State = ConversationState.Queued,
            Owner = null,
            CreatedAt = _clock.UtcNow,
            // Reporting parity with W5 digital; VOICE front-of-queue ordering is via QUEUE_PRIO above,
            // not this column (which only orders the digital QueueDistributionWorker).
            QueuePriority = -1,
        };
        conversation.SetMetadata("direction", "callback-rescue");
        conversation.SetMetadata("rescuedFrom", rescuedFrom.Value);
        conversation.SetMetadata("callbackAttempts", callbackAttempts.ToString(CultureInfo.InvariantCulture));
        await _conversations.SaveAsync(conversation, ct).ConfigureAwait(false);

        LogCallback(conversationId.Value, dialNumber, asteriskQueue, rescuedFrom.Value);
        return true;
    }

    private async Task<EntityId> ResolveContactIdAsync(TenantId tenantId, string number, EntityId rescuedFrom, CancellationToken ct)
    {
        var original = await _conversations.GetByIdAsync(tenantId, rescuedFrom, ct).ConfigureAwait(false);
        if (original is not null)
            return original.ContactId;

        var contact = await _contacts.ResolveAsync(tenantId, new ChannelAddress(ChannelType.Voice, number), ct).ConfigureAwait(false);
        return contact.ContactId;
    }

    /// <summary>Keeps digits and a leading <c>+</c> only — strips spaces, dashes, and parentheses.</summary>
    private static string NormalizeNumber(string raw) =>
        new([.. raw.Where(c => char.IsDigit(c) || c == '+')]);

    [LoggerMessage(EventId = 9150, Level = LogLevel.Warning,
        Message = "[VOICE-RESCUE] Origin queue {QueueId} not found for rescue of {RescuedFrom} — aborting callback")]
    private partial void LogQueueNotFound(string queueId, string rescuedFrom);

    [LoggerMessage(EventId = 9151, Level = LogLevel.Warning,
        Message = "[VOICE-RESCUE] Outbound route resolve for {Number} failed (falling back to default trunk): {Reason}")]
    private partial void LogRouteFailed(string number, string reason);

    [LoggerMessage(EventId = 9152, Level = LogLevel.Warning,
        Message = "[VOICE-RESCUE] No trunk available to call back {Number} (rescue of {RescuedFrom}) — aborting")]
    private partial void LogNoTrunk(string number, string rescuedFrom);

    [LoggerMessage(EventId = 9153, Level = LogLevel.Warning,
        Message = "[VOICE-RESCUE] Originate for rescue conversation {ConversationId} ({Number}) threw: {Reason}")]
    private partial void LogOriginateThrew(string conversationId, string number, string reason);

    [LoggerMessage(EventId = 9154, Level = LogLevel.Warning,
        Message = "[VOICE-RESCUE] Originate for rescue conversation {ConversationId} ({Number}) rejected: {Reason}")]
    private partial void LogOriginateRejected(string conversationId, string number, string reason);

    [LoggerMessage(EventId = 9155, Level = LogLevel.Information,
        Message = "[VOICE-RESCUE] Conversation {ConversationId}: calling back {Number} into queue {Queue} at front (rescue of {RescuedFrom})")]
    private partial void LogCallback(string conversationId, string number, string queue, string rescuedFrom);
}
