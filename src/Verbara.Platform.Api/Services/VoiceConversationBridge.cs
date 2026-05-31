using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Projects the live AMI call-session pipeline (<see cref="ICallSessionManager"/>) onto
/// first-class voice <c>Conversation</c>s (Phase 3B.0). For an inbound queue call it:
/// <list type="number">
///   <item>resolves the owning tenant from the <c>TENANT_ID</c> channel variable via an AMI
///   <c>GetVar</c> on the inbound trunk leg (the Phase-2.4 contract) and stamps it onto the
///   live <see cref="CallSession.TenantId"/> hook — fail-closed if unresolved;</item>
///   <item>resolves/creates the calling contact and creates a tracked voice Conversation,
///   idempotent per call via the Asterisk <c>LinkedId</c> (<see cref="Conversation.VoiceLinkedId"/>);</item>
///   <item>follows the call lifecycle (Queued → Offered → Active on answer; → WrapUp / Abandoned
///   on hangup), assigns the answering agent as owner, reserves/releases voice capacity, and
///   drives the agent presence Available → Busy → ACW transitions (ACW auto-pauses the realtime
///   queue member via <see cref="RealtimeStateBridge"/>).</item>
/// </list>
///
/// <para><b>Warm-standby, leader-emit (the AMI broadcast contract):</b> every pod's
/// <see cref="ICallSessionManager"/> observes the same broadcast AMI stream, so each handler
/// short-circuits unless this pod holds the <see cref="VoiceLeaderResources.AmiOwner"/> lease.
/// Only the leader emits side-effects (and stamps the tenant), so they happen exactly once
/// cluster-wide with no cold-start gap on failover. The per-call <c>voice_linked_id</c> unique
/// index (migration 027) is the failover safety net behind this gate. On SMB single-host the
/// lease is always held by the only pod, so correctness is free.</para>
///
/// <para><b>StopHost safety:</b> the subscription's <c>OnNext</c> is synchronous and never throws
/// (fire-and-forget into a fully-guarded async handler), so an event-handling fault can never
/// bubble out and trip <c>BackgroundServiceExceptionBehavior.StopHost</c>.</para>
/// </summary>
internal sealed partial class VoiceConversationBridge : IHostedService, IDisposable
{
    private const string TenantVariable = "TENANT_ID";
    private const string AnonymousCaller = "anonymous";

    private readonly ICallSessionManager _sessions;
    private readonly VerbaraServerPool _serverPool;
    private readonly IConversationStore _conversations;
    private readonly IContactIdentityResolver _contacts;
    private readonly IAgentStore _agents;
    private readonly IAgentCapacityService _capacity;
    private readonly PlatformEventBus _eventBus;
    private readonly IClusterLeader _leader;
    private readonly IClock _clock;
    private readonly ILogger<VoiceConversationBridge> _logger;

    // Serializes the handling of events for the SAME call: OnNext fans out fire-and-forget async
    // handlers that interleave at await points, so without this a Connected/Ended pair for one call
    // could clobber each other's Conversation save. A FIXED stripe pool (indexed by SessionId hash)
    // — not a per-SessionId dictionary — never grows, needs no per-call cleanup, and has no
    // dispose/release race; the only cost is rare, harmless contention between unrelated calls that
    // hash to the same stripe. Disposed once with the bridge.
    private const int CallLockStripes = 64;
    private readonly SemaphoreSlim[] _callLocks =
        [.. Enumerable.Range(0, CallLockStripes).Select(_ => new SemaphoreSlim(1, 1))];
    private IDisposable? _subscription;

    private SemaphoreSlim CallLockFor(string sessionId) =>
        _callLocks[(int)((uint)sessionId.GetHashCode() % (uint)CallLockStripes)];

    public VoiceConversationBridge(
        ICallSessionManager sessions,
        VerbaraServerPool serverPool,
        IConversationStore conversations,
        IContactIdentityResolver contacts,
        IAgentStore agents,
        IAgentCapacityService capacity,
        PlatformEventBus eventBus,
        [FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader leader,
        IClock clock,
        ILogger<VoiceConversationBridge> logger)
    {
        _sessions = sessions;
        _serverPool = serverPool;
        _conversations = conversations;
        _contacts = contacts;
        _agents = agents;
        _capacity = capacity;
        _eventBus = eventBus;
        _leader = leader;
        _clock = clock;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _subscription = _sessions.Events.Subscribe(OnEvent);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        foreach (var stripe in _callLocks)
            stripe.Dispose();
    }

    // Synchronous, never-throwing OnNext: dispatches a fully-guarded async handler.
    internal void OnEvent(SessionDomainEvent evt) => _ = HandleEventAsync(evt);

    internal async Task HandleEventAsync(SessionDomainEvent evt)
    {
        // Warm-standby leader-emit gate: only the AMI-owner pod produces side-effects.
        if (!_leader.IsLeader)
            return;

        // Only the three lifecycle events the voice projection follows.
        var isLifecycle = evt is CallQueuedEvent or CallConnectedEvent or CallEndedEvent;
        if (!isLifecycle)
            return;

        var gate = CallLockFor(evt.SessionId);
        var acquired = false;
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            switch (evt)
            {
                case CallQueuedEvent queued:
                    await OnCallQueuedAsync(queued).ConfigureAwait(false);
                    break;
                case CallConnectedEvent connected:
                    await OnCallConnectedAsync(connected).ConfigureAwait(false);
                    break;
                case CallEndedEvent ended:
                    await OnCallEndedAsync(ended).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            LogHandlerError(evt.GetType().Name, evt.SessionId, ex.Message, ex);
        }
        finally
        {
            if (acquired)
                gate.Release();
        }
    }

    /// <summary>Inbound call entered the Asterisk queue — create the tracked voice Conversation.</summary>
    private async Task OnCallQueuedAsync(CallQueuedEvent evt)
    {
        var session = _sessions.GetById(evt.SessionId);
        if (session is null || session.Direction != CallDirection.Inbound)
            return;

        var tenant = await ResolveTenantAsync(session).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tenant))
        {
            LogTenantUnresolved(evt.SessionId);
            return; // fail-closed: never create a Conversation without the real tenant
        }

        var tenantId = new TenantId(tenant);

        // Idempotent per call (LinkedId): a re-emission for the same physical call is a no-op.
        var existing = await _conversations.FindByVoiceLinkedIdAsync(tenantId, session.LinkedId, CancellationToken.None).ConfigureAwait(false);
        if (existing is not null)
            return;

        // Withheld / anonymous caller-id (CLIR) has no number to identify the caller. By design for
        // 3B.0 these calls share one per-tenant "anonymous" voice Contact (the conventional CCaaS
        // bucket) — the Conversations stay distinct (correlated by the per-call voice_linked_id), only
        // the contact identity is shared. Per-caller separation for withheld-CID is deferred to 3B.1
        // (screen-pop / contact UX). Tenant-scoped, so no cross-tenant bleed.
        var callerNumber = string.IsNullOrWhiteSpace(session.CallerIdNum) ? AnonymousCaller : session.CallerIdNum!;
        var contact = await _contacts.ResolveAsync(tenantId, new ChannelAddress(ChannelType.Voice, callerNumber), CancellationToken.None).ConfigureAwait(false);

        var conversation = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = tenantId,
            ContactId = contact.ContactId,
            Channel = ChannelType.Voice,
            State = ConversationState.Queued,
            CreatedAt = _clock.UtcNow,
            VoiceLinkedId = session.LinkedId,
        };
        await _conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);

        _eventBus.Publish(new ConversationStateChangedEvent(tenant, conversation.ConversationId.Value, "", nameof(ConversationState.Queued)));
        LogConversationCreated(conversation.ConversationId.Value, tenant, session.LinkedId);
    }

    /// <summary>Agent answered — advance the Conversation to Active, assign the owner, reserve capacity, go Busy.</summary>
    private async Task OnCallConnectedAsync(CallConnectedEvent evt)
    {
        var session = _sessions.GetById(evt.SessionId);
        if (session is null || session.Direction != CallDirection.Inbound)
            return;

        var tenant = await ResolveTenantAsync(session).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tenant))
            return;

        var tenantId = new TenantId(tenant);
        var agentId = ExtractAgentId(tenant, session.AgentInterface);

        var conversation = await _conversations.FindByVoiceLinkedIdAsync(tenantId, session.LinkedId, CancellationToken.None).ConfigureAwait(false);
        if (conversation is null)
        {
            LogConversationMissing(evt.SessionId, nameof(CallConnectedEvent));
            return;
        }

        // The SDK emits ≥2 CallConnectedEvents for one answered queue call (the agent-connect path
        // is unconditional), and a leadership failover can re-emit too. The Queued→Offered→Active
        // advance is the once-per-call signal: only the FIRST delivery actually moves the state, so
        // gating the owner-assign + persist + capacity-reserve + Busy on it makes them idempotent.
        var oldState = conversation.State;
        if (ConversationStateMachine.CanTransition(conversation.State, ConversationState.Offered))
            conversation.TransitionTo(ConversationState.Offered);
        if (ConversationStateMachine.CanTransition(conversation.State, ConversationState.Active))
            conversation.TransitionTo(ConversationState.Active);
        var becameActive = oldState != ConversationState.Active && conversation.State == ConversationState.Active;
        if (!becameActive)
            return; // re-delivered / out-of-order Connected for an already-active call → no-op

        if (agentId is { } owner)
            conversation.Owner = new ConversationOwner(ConversationOwnerKind.Agent, owner);
        conversation.UpdatedAt = _clock.UtcNow;
        await _conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
        _eventBus.Publish(new ConversationStateChangedEvent(tenant, conversation.ConversationId.Value, oldState.ToString(), conversation.State.ToString()));

        if (agentId is { } reserved)
        {
            await ReserveCapacityAsync(tenantId, reserved).ConfigureAwait(false);
            await TransitionAgentAsync(tenantId, reserved, AgentState.Busy).ConfigureAwait(false);
        }
    }

    /// <summary>Call ended — wrap up / abandon the Conversation, release capacity, go ACW.</summary>
    private async Task OnCallEndedAsync(CallEndedEvent evt)
    {
        var session = _sessions.GetById(evt.SessionId);
        if (session is null || session.Direction != CallDirection.Inbound)
            return;

        // Recover the tracked Conversation by the call-global LinkedId. On a leadership failover
        // mid-call this pod may never have stamped session.TenantId (it was a follower during
        // Queued/Connected) and the trunk channel is already gone (AMI re-resolution impossible),
        // so fall back to a cross-tenant lookup by LinkedId to stay lossless on failover.
        var conversation = string.IsNullOrEmpty(session.TenantId)
            ? await _conversations.FindByVoiceLinkedIdAcrossTenantsAsync(session.LinkedId, CancellationToken.None).ConfigureAwait(false)
            : await _conversations.FindByVoiceLinkedIdAsync(new TenantId(session.TenantId), session.LinkedId, CancellationToken.None).ConfigureAwait(false);
        if (conversation is null)
            return; // untracked call — nothing to wrap up

        var tenantId = conversation.TenantId;
        var agentId = ExtractAgentId(tenantId.Value, session.AgentInterface);
        var wasActive = conversation.State == ConversationState.Active;

        // Answered call → WrapUp (awaiting disposition, 3B.1); never-answered → Abandoned.
        ConversationState? target = conversation.State switch
        {
            ConversationState.Active => ConversationState.WrapUp,
            ConversationState.Queued or ConversationState.Offered => ConversationState.Abandoned,
            _ => null,
        };
        if (target is { } next && ConversationStateMachine.CanTransition(conversation.State, next))
        {
            var oldState = conversation.State;
            conversation.TransitionTo(next, _clock.UtcNow);
            conversation.UpdatedAt = _clock.UtcNow;
            await _conversations.SaveAsync(conversation, CancellationToken.None).ConfigureAwait(false);
            _eventBus.Publish(new ConversationStateChangedEvent(tenantId.Value, conversation.ConversationId.Value, oldState.ToString(), conversation.State.ToString()));
        }

        // Release capacity + go ACW only for an answered (Active) call — symmetric with the
        // Connected handler's reserve+Busy, so a never-answered (Abandoned) call neither releases
        // capacity nor transitions an agent who never went Busy.
        if (wasActive && agentId is { } released)
        {
            await ReleaseCapacityAsync(tenantId, released).ConfigureAwait(false);
            await TransitionAgentAsync(tenantId, released, AgentState.ACW).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the resolved tenant, reading + stamping <see cref="CallSession.TenantId"/> on first
    /// resolution. Reads the <c>TENANT_ID</c> channel variable (set via the trunk's
    /// <c>ps_endpoints set_var</c>, Phase 2.4) off the inbound trunk leg via an AMI <c>GetVar</c>.
    /// Returns <see langword="null"/> (fail-closed) when AMI is unavailable or the variable is unset.
    /// </summary>
    private async Task<string?> ResolveTenantAsync(CallSession session)
    {
        if (!string.IsNullOrEmpty(session.TenantId))
            return session.TenantId;

        var trunk = session.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Caller)
                    ?? (session.Participants.Count > 0 ? session.Participants[0] : null);
        if (trunk is null)
            return null;

        return await ResolveTenantFromChannelAsync(session, trunk.Channel).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the <c>TENANT_ID</c> channel variable on <paramref name="channel"/> via an AMI GetVar
    /// and, on success, stamps it onto <see cref="CallSession.TenantId"/> (the SDK's reserved hook).
    /// Returns <see langword="null"/> (fail-closed) when AMI is unavailable, the variable is unset,
    /// or the action fails. Internal so the resolution wire (the linchpin) is unit-testable without
    /// a live SDK-internal participant.
    /// </summary>
    internal async Task<string?> ResolveTenantFromChannelAsync(CallSession session, string channel)
    {
        var server = _serverPool.GetServer("primary");
        if (server is null)
            return null; // AMI not connected / unconfigured — fail-closed

        try
        {
            var response = await server.Connection.SendActionAsync<GetVarResponse>(
                new GetVarAction { Channel = channel, Variable = TenantVariable },
                CancellationToken.None).ConfigureAwait(false);

            var ok = string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase);
            if (!ok || string.IsNullOrEmpty(response.Value))
                return null;

            // Stamp the SDK's reserved tenant hook so downstream observers on THIS (leader) pod —
            // CDR projection, AgentAssist — resolve the same tenant; followers never stamp, so
            // their fail-closed paths keep side-effects single cluster-wide.
            session.TenantId = response.Value;
            return response.Value;
        }
        catch (Exception ex)
        {
            LogGetVarFailed(channel, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extracts the platform agent id from the realtime member interface
    /// <c>PJSIP/{tenant}-agent-{agentId}</c>. The SDK's <c>CallSession.AgentId</c> is the raw AMI
    /// agent field (often empty for app_queue), so the interface is the reliable source.
    /// </summary>
    private static EntityId? ExtractAgentId(string tenant, string? agentInterface)
    {
        if (string.IsNullOrEmpty(agentInterface))
            return null;

        var prefix = $"PJSIP/{tenant}-agent-";
        if (!agentInterface.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var id = agentInterface[prefix.Length..];
        // IsNullOrWhiteSpace (not IsNullOrEmpty): EntityId.From throws on a whitespace-only suffix,
        // and a malformed agent interface must not abort the whole handler — return null so the
        // conversation lifecycle still advances (per-step fault isolation).
        return string.IsNullOrWhiteSpace(id) ? null : EntityId.From(id);
    }

    private async Task TransitionAgentAsync(TenantId tenantId, EntityId agentId, AgentState target)
    {
        try
        {
            var agent = await _agents.GetByIdAsync(tenantId, agentId, CancellationToken.None).ConfigureAwait(false);
            if (agent is null)
            {
                LogAgentMissing(agentId.Value, tenantId.Value);
                return;
            }

            // Guarded: e.g. only Available → Busy and Busy → ACW are legal; skip otherwise
            // (an agent who manually changed state, or a second call leg).
            if (!AgentStateMachine.CanTransition(agent.State, target))
                return;

            var oldState = agent.State;
            agent.TransitionTo(target);
            agent.UpdatedAt = _clock.UtcNow;
            await _agents.SaveAsync(agent, CancellationToken.None).ConfigureAwait(false);

            // RealtimeStateBridge reacts to this: ACW (non-routable) pauses the realtime queue
            // member; Busy (routable) leaves it unpaused (Asterisk device state blocks re-dial
            // during the live call).
            _eventBus.Publish(new AgentStateChangedEvent(tenantId.Value, agent.AgentId.Value, agent.DisplayName, oldState.ToString(), target.ToString()));
        }
        catch (Exception ex)
        {
            LogAgentTransitionFailed(agentId.Value, target.ToString(), ex.Message);
        }
    }

    private async Task ReserveCapacityAsync(TenantId tenantId, EntityId agentId)
    {
        try
        {
            await _capacity.ReserveAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCapacityFailed(agentId.Value, "reserve", ex.Message);
        }
    }

    private async Task ReleaseCapacityAsync(TenantId tenantId, EntityId agentId)
    {
        try
        {
            await _capacity.ReleaseAsync(tenantId, agentId, ChannelType.Voice, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCapacityFailed(agentId.Value, "release", ex.Message);
        }
    }

    // ─── Log messages ───────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] Session {SessionId}: tenant could not be resolved (TENANT_ID unset) — not tracking the call.")]
    private partial void LogTenantUnresolved(string sessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[VOICE-CONV] Created voice Conversation {ConversationId} for tenant {TenantId} (call {LinkedId}).")]
    private partial void LogConversationCreated(string conversationId, string tenantId, string linkedId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] Session {SessionId}: no tracked Conversation found on {Event} — skipping its update.")]
    private partial void LogConversationMissing(string sessionId, string @event);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] AMI GetVar TENANT_ID failed on channel {Channel}: {Reason}")]
    private partial void LogGetVarFailed(string channel, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] Agent {AgentId} not found in tenant {TenantId} — skipping presence transition.")]
    private partial void LogAgentMissing(string agentId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] Agent {AgentId} presence transition to {Target} failed: {Reason}")]
    private partial void LogAgentTransitionFailed(string agentId, string target, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] Voice capacity {Operation} for agent {AgentId} failed: {Reason}")]
    private partial void LogCapacityFailed(string agentId, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[VOICE-CONV] Error handling {Event} for session {SessionId}: {Reason}")]
    private partial void LogHandlerError(string @event, string sessionId, string reason, Exception ex);
}
