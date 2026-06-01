using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Compliance;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Services;

/// <summary>Result of an agent click-to-dial — <see cref="CorrelationId"/> is the tracked outbound Conversation id.</summary>
internal sealed record VoiceDialOutcome(bool Accepted, string? CorrelationId, string? Error);

internal interface IAgentOutboundDialService
{
    Task<VoiceDialOutcome> DialAsync(
        TenantId tenantId, EntityId agentId, string toNumber, EntityId? contactId, CancellationToken ct);
}

/// <summary>
/// Agent click-to-dial (3B.2d): server-side AMI Originate of an outbound call, REUSING the Pro Dialer
/// stack (DNC compliance + outbound route→trunk resolution + the circuit-breaker/trunk-health
/// <see cref="OriginateExecutorBase"/>) rather than the browser <c>SimpleUser.call</c>, which would
/// bypass caller-ID, trunk selection, DNC, and call tracking. The Originate rings the AGENT's endpoint
/// as the A-leg (so the softphone rings and the agent answers), then the <c>[outbound-agent]</c>
/// dialplan dials the customer over the resolved trunk with the tenant outbound caller ID.
///
/// <para>The originated call is correlated back to a tracked Conversation by stamping
/// <c>VERBARA_OUTBOUND_ID = the pre-generated Conversation id</c> as a channel variable; the
/// <see cref="VoiceConversationBridge"/> reads it on <c>CallConnected</c> and links by id (no new
/// index/migration — 3B.2d.3). The Conversation row is created only AFTER a successful Originate, so a
/// rejected dial never leaves an orphan.</para>
///
/// <para>Leader-gated through the same <c>voice:ami:owner:leader</c> lease as the bridge/transfer so the
/// AMI side-effect is funneled through a single writer; on SMB single-host the only pod always holds it.
/// DNC + route resolvers are OPTIONAL (only registered with the full dialer Postgres storage): a
/// voice-only tenant has neither, so DNC fails open and the trunk falls back to the configured default.</para>
/// </summary>
internal sealed partial class AgentOutboundDialService : IAgentOutboundDialService
{
    private const string OutboundAgentContext = "outbound-agent";
    private const string TenantVariable = "TENANT_ID";
    private const string AgentVariable = "AGENT_ID";
    private const string OutboundIdVariable = "VERBARA_OUTBOUND_ID";
    private const string TrunkVariable = "TRUNK";
    private const string CallerIdVariable = "OUTBOUND_CALLERID";
    private const string PrimaryNode = "primary";
    private const long OriginateTimeoutMs = 30_000;

    private readonly IConversationStore _conversations;
    private readonly IContactIdentityResolver _contacts;
    private readonly IAgentStore _agents;
    private readonly ITenantStore _tenants;
    private readonly OriginateExecutorBase _originate;
    private readonly OutboundRouteResolverBase? _routes;
    private readonly DncCheckerBase? _dnc;
    private readonly IClusterLeader _leader;
    private readonly IClock _clock;
    private readonly string? _defaultTrunk;
    private readonly ILogger<AgentOutboundDialService> _logger;

    public AgentOutboundDialService(
        IConversationStore conversations,
        IContactIdentityResolver contacts,
        IAgentStore agents,
        ITenantStore tenants,
        OriginateExecutorBase originate,
        OutboundRouteResolverBase? routes,
        DncCheckerBase? dnc,
        IClusterLeader leader,
        IClock clock,
        string? defaultTrunk,
        ILogger<AgentOutboundDialService> logger)
    {
        _conversations = conversations;
        _contacts = contacts;
        _agents = agents;
        _tenants = tenants;
        _originate = originate;
        _routes = routes;
        _dnc = dnc;
        _leader = leader;
        _clock = clock;
        _defaultTrunk = defaultTrunk;
        _logger = logger;
    }

    public async Task<VoiceDialOutcome> DialAsync(
        TenantId tenantId, EntityId agentId, string toNumber, EntityId? contactId, CancellationToken ct)
    {
        // Single-writer AMI gate (mirrors VoiceConversationBridge / VoiceCallControlService).
        if (!_leader.IsLeader)
            return new VoiceDialOutcome(false, null, "not-leader");

        var number = NormalizeNumber(toNumber);
        if (string.IsNullOrEmpty(number))
            return new VoiceDialOutcome(false, null, "invalid-number");

        var agent = await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
        if (agent is null)
            return new VoiceDialOutcome(false, null, "agent-not-found");

        // DNC compliance. Optional resolver (only with dialer Postgres); a store error fails OPEN so a
        // transient outage never blocks dialing — the explicit blocked==true is the only hard stop.
        if (_dnc is not null)
        {
            try
            {
                var blocked = await _dnc.IsBlockedAsync(tenantId.Value, number, dncListId: null, checkGlobal: true, ct).ConfigureAwait(false);
                if (blocked)
                    return new VoiceDialOutcome(false, null, "dnc-blocked");
            }
            catch (Exception ex)
            {
                LogDncFailed(number, ex.Message);
            }
        }

        // Outbound route → trunk. Optional resolver; fall back to the configured default trunk.
        string? trunk = null;
        if (_routes is not null)
        {
            try
            {
                var resolved = await _routes.ResolveAsync(tenantId.Value, number, campaignId: null, ct).ConfigureAwait(false);
                trunk = resolved?.Name;
            }
            catch (Exception ex)
            {
                LogRouteFailed(number, ex.Message);
            }
        }
        trunk ??= _defaultTrunk;
        if (string.IsNullOrWhiteSpace(trunk))
            return new VoiceDialOutcome(false, null, "no-trunk");

        // Caller ID: the tenant outbound setting (3B.2d.1). Empty → the dialplan clears CALLERID and the
        // trunk's own default is presented.
        var tenant = await _tenants.GetAsync(tenantId.Value, ct).ConfigureAwait(false);
        var callerId = tenant?.Options.OutboundCallerId ?? "";

        // Resolve/create the dialed contact (reuse the inbound identity resolver) unless one was given.
        var resolvedContactId = contactId;
        if (resolvedContactId is null)
        {
            var contact = await _contacts.ResolveAsync(tenantId, new ChannelAddress(ChannelType.Voice, number), ct).ConfigureAwait(false);
            resolvedContactId = contact.ContactId;
        }

        // The Conversation id IS the correlation id stamped on the Originate — the bridge links by id
        // (3B.2d.3). Generated up front so the channel var carries it; the row is persisted only after a
        // successful Originate so a rejected dial leaves no orphan.
        var conversationId = EntityId.New();

        var action = new OriginateAction
        {
            Channel = $"PJSIP/{tenantId.Value}-agent-{agentId.Value}",
            Context = OutboundAgentContext,
            Exten = number,
            Priority = 1,
            IsAsync = true,
            Timeout = OriginateTimeoutMs,
        };
        action.SetVariable(TenantVariable, tenantId.Value);
        action.SetVariable(AgentVariable, agentId.Value);
        action.SetVariable(OutboundIdVariable, conversationId.Value);
        action.SetVariable(TrunkVariable, trunk!);
        action.SetVariable(CallerIdVariable, callerId);

        OriginateResult result;
        try
        {
            result = await _originate.ExecuteAsync(action, PrimaryNode, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOriginateFailed(conversationId.Value, ex.Message);
            return new VoiceDialOutcome(false, null, "originate-failed");
        }

        if (!result.Success)
            return new VoiceDialOutcome(false, null, result.ErrorMessage ?? "originate-rejected");

        var conversation = new Conversation
        {
            ConversationId = conversationId,
            TenantId = tenantId,
            ContactId = resolvedContactId.Value,
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            Owner = new ConversationOwner(ConversationOwnerKind.Agent, agentId),
            CreatedAt = _clock.UtcNow,
        };
        conversation.SetMetadata("direction", "outbound");
        await _conversations.SaveAsync(conversation, ct).ConfigureAwait(false);

        LogDial(conversationId.Value, agentId.Value, number);
        return new VoiceDialOutcome(true, conversationId.Value, null);
    }

    /// <summary>Keeps digits and a leading <c>+</c> only — strips spaces, dashes, and parentheses.</summary>
    private static string NormalizeNumber(string raw) =>
        new([.. raw.Where(c => char.IsDigit(c) || c == '+')]);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[VOICE-DIAL] Conversation {ConversationId}: agent {AgentId} dialing {Number}")]
    private partial void LogDial(string conversationId, string agentId, string number);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-DIAL] DNC check for {Number} failed (failing open): {Reason}")]
    private partial void LogDncFailed(string number, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-DIAL] Outbound route resolve for {Number} failed (falling back to default trunk): {Reason}")]
    private partial void LogRouteFailed(string number, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-DIAL] Originate for pending conversation {ConversationId} threw: {Reason}")]
    private partial void LogOriginateFailed(string conversationId, string reason);
}
