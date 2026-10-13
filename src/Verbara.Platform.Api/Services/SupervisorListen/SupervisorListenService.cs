using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using OriginateResult = Verbara.Sdk.Pro.Dialer.Execution.OriginateResult;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>A start or current answer: the listen, or a refusal.</summary>
internal sealed record SupervisorListenResult(SupervisorListenDto? Listen, SupervisorListenRefusal? Refusal)
{
    public static SupervisorListenResult Refused(string code, string? conversationId, string? listenId = null) =>
        new(null, SupervisorListenRefusal.For(code, conversationId, listenId));
}

/// <summary>A stop answer: the stopped listen, or a refusal.</summary>
internal sealed record SupervisorListenStopResult(SupervisorListenStoppedDto? Stopped, SupervisorListenRefusal? Refusal);

/// <summary>
/// supervisor-silent-listen — the listen core (design D1, D4, D6, D7, D10, D11). A start originates the supervisor's
/// destination — their receive-only listen endpoint (B) or their admin-stored phone (C) — into the static
/// <c>[supervisor-listen]</c> dialplan context, which ChanSpies the agent's live leg with the literal options
/// <c>qESu</c>. The only variable is the destination.
/// <para>
/// Leader-only: every operation refuses <c>not-leader</c> unless this replica holds the voice AMI lease. Silent by
/// construction: this service has no event bus, hub or switchboard dependency, and it never writes the conversation;
/// its only effects are the AMI originate (and hangup of its own leg), the registry and the audit trail.
/// </para>
/// <para>
/// The route-level checks (<c>not-permitted</c>, <c>invalid-destination</c>, <c>voice-unavailable</c>) run in the
/// handler before this service; the rest follow the fixture's precedence: <c>not-leader</c>,
/// <c>conversation-not-found</c>, <c>no-voice-call</c>, <c>already-listening</c>,
/// <c>no-supervisor-endpoint</c> | <c>no-listen-phone</c>, <c>no-trunk</c>, <c>originate-failed</c>.
/// </para>
/// </summary>
internal sealed partial class SupervisorListenService
{
    private const string ListenContext = "supervisor-listen";
    private const string SpyTargetVariable = "SPY_TARGET";
    private const string PrimaryNode = "primary";

    /// <summary>How long the supervisor's leg may ring (design D6: 20 s).</summary>
    public static readonly TimeSpan OriginateTimeout = TimeSpan.FromSeconds(20);

    private readonly IConversationStore _conversations;
    private readonly ISupervisorListenProfileStore _profiles;
    private readonly IVoiceCallLegs _legs;
    private readonly OriginateExecutorBase _originate;
    private readonly OutboundRouteResolverBase? _routes;
    private readonly ITenantStore _tenants;
    private readonly IClusterLeader _leader;
    private readonly IClock _clock;
    private readonly string? _defaultTrunk;
    private readonly SupervisorListenRegistry _registry;
    private readonly SupervisorListenAudit _audit;
    private readonly ISupervisorListenLegControl _legControl;
    private readonly ILogger<SupervisorListenService> _logger;

    public SupervisorListenService(
        IConversationStore conversations,
        ISupervisorListenProfileStore profiles,
        IVoiceCallLegs legs,
        OriginateExecutorBase originate,
        OutboundRouteResolverBase? routes,
        ITenantStore tenants,
        IClusterLeader leader,
        IClock clock,
        string? defaultTrunk,
        SupervisorListenRegistry registry,
        SupervisorListenAudit audit,
        ISupervisorListenLegControl legControl,
        ILogger<SupervisorListenService> logger)
    {
        _conversations = conversations;
        _profiles = profiles;
        _legs = legs;
        _originate = originate;
        _routes = routes;
        _tenants = tenants;
        _leader = leader;
        _clock = clock;
        _defaultTrunk = defaultTrunk;
        _registry = registry;
        _audit = audit;
        _legControl = legControl;
        _logger = logger;
    }

    /// <summary>The endpoint id (and AOR) of a supervisor's listen endpoint (design D3).</summary>
    public static string EndpointId(TenantId tenantId, EntityId userId) => $"{tenantId.Value}-agent-sup-{userId.Value}";

    public async Task<SupervisorListenResult> StartAsync(
        ClaimsPrincipal caller, TenantId tenantId, EntityId supervisorUserId, string conversationId, string destination,
        CancellationToken ct)
    {
        if (!_leader.IsLeader)
            return SupervisorListenResult.Refused(SupervisorListenRefusal.NotLeader, conversationId);

        // A conversation of another tenant is never read: the lookup is tenant-scoped, so it answers as an unknown one.
        var conversation = await _conversations.GetByIdAsync(tenantId, EntityId.From(conversationId), ct).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != tenantId)
            return SupervisorListenResult.Refused(SupervisorListenRefusal.ConversationNotFound, conversationId);

        var agentLeg = ResolveAgentLeg(tenantId, conversation);
        if (agentLeg is null)
            return SupervisorListenResult.Refused(SupervisorListenRefusal.NoVoiceCall, conversationId);

        var profile = await _profiles.GetAsync(tenantId, supervisorUserId, ct).ConfigureAwait(false);
        var listen = new ActiveListen
        {
            ListenId = EntityId.New().Value,
            TenantId = tenantId,
            SupervisorUserId = supervisorUserId,
            ConversationId = conversation.ConversationId.Value,
            Destination = destination,
            AgentId = conversation.Owner!.OwnerId!.Value.Value,
            PhoneLast4 = destination == ListenVocabulary.Phone && profile?.PhoneNumber is { Length: >= 4 } phone ? phone[^4..] : null,
            StartedAt = _clock.UtcNow,
        };

        // Reserve the supervisor's single slot before anything is dialed, so two concurrent starts cannot both dial.
        if (!_registry.TryAdd(listen, out var existing))
            return SupervisorListenResult.Refused(SupervisorListenRefusal.AlreadyListening, existing!.ConversationId, existing.ListenId);

        var refusal = await BuildAndOriginateAsync(listen, profile, agentLeg, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            _registry.TryEnd(listen.ListenId, _clock.UtcNow);
            return SupervisorListenResult.Refused(refusal, conversationId);
        }

        LogListenStarted(listen.ListenId, conversationId, destination, tenantId.Value);
        await _audit.StartedAsync(listen, caller, ct).ConfigureAwait(false);
        return new SupervisorListenResult(ToDto(listen), null);
    }

    public SupervisorListenResult GetCurrent(TenantId tenantId, EntityId supervisorUserId)
    {
        if (!_leader.IsLeader)
            return SupervisorListenResult.Refused(SupervisorListenRefusal.NotLeader, null);

        var listen = _registry.GetBySupervisor(tenantId, supervisorUserId);
        return listen is null
            ? SupervisorListenResult.Refused(SupervisorListenRefusal.ListenNotFound, null)
            : new SupervisorListenResult(ToDto(listen), null);
    }

    public async Task<SupervisorListenStopResult> StopAsync(
        ClaimsPrincipal caller, TenantId tenantId, EntityId supervisorUserId, string conversationId, CancellationToken ct)
    {
        if (!_leader.IsLeader)
            return new(null, SupervisorListenRefusal.For(SupervisorListenRefusal.NotLeader, conversationId));

        var listen = _registry.GetBySupervisor(tenantId, supervisorUserId);
        if (listen is null || !string.Equals(listen.ConversationId, conversationId, StringComparison.Ordinal)
            || _registry.TryEnd(listen.ListenId, _clock.UtcNow) is null)
            return new(null, SupervisorListenRefusal.For(SupervisorListenRefusal.ListenNotFound, conversationId));

        var stoppedAt = _clock.UtcNow;
        await _legControl.HangupAsync(listen, ct).ConfigureAwait(false);
        LogListenStopped(listen.ListenId, conversationId, tenantId.Value);
        await _audit.StoppedAsync(listen, ListenVocabulary.Stopped, stoppedAt, caller, ct).ConfigureAwait(false);
        return new(new SupervisorListenStoppedDto(listen.ListenId, listen.ConversationId, stoppedAt, ListenVocabulary.Stopped), null);
    }

    /// <summary>The uniqueid of the owner agent's live leg of a voice call that is Active or OnHold; null otherwise.</summary>
    private string? ResolveAgentLeg(TenantId tenantId, Conversation conversation)
    {
        if (conversation.Channel != ChannelType.Voice
            || conversation.State is not (ConversationState.Active or ConversationState.OnHold)
            || string.IsNullOrEmpty(conversation.VoiceLinkedId))
            return null;
        return AgentLegResolver.Resolve(tenantId.Value, conversation.Owner, _legs.GetParticipants(conversation.VoiceLinkedId));
    }

    /// <summary>Builds the destination's originate and sends it; returns a refusal code, or null when it was accepted.</summary>
    private async Task<string?> BuildAndOriginateAsync(
        ActiveListen listen, SupervisorListenProfile? profile, string agentLeg, CancellationToken ct)
    {
        var action = new OriginateAction
        {
            Context = ListenContext,
            Exten = listen.Destination,
            Priority = 1,
            IsAsync = true,
            Timeout = (long)OriginateTimeout.TotalMilliseconds,
            ChannelId = listen.ListenId,
        };

        if (listen.Destination == ListenVocabulary.Endpoint)
        {
            if (profile is null || !profile.HasCredentials)
                return SupervisorListenRefusal.NoSupervisorEndpoint;
            action.Channel = $"PJSIP/{EndpointId(listen.TenantId, listen.SupervisorUserId)}";
            action.CallerId = $"\"{SupervisorListenEndpointDto.CallerName}\" <{listen.ConversationId}>";
        }
        else
        {
            if (string.IsNullOrEmpty(profile?.PhoneNumber))
                return SupervisorListenRefusal.NoListenPhone;
            var trunk = await ResolveTrunkAsync(listen.TenantId, profile.PhoneNumber, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(trunk))
                return SupervisorListenRefusal.NoTrunk;

            // Lab 0.3(g): on Asterisk 22 an Originate of PJSIP/{trunk}/{number} fails; PJSIP/{number}@{trunk} works.
            action.Channel = $"PJSIP/{profile.PhoneNumber}@{trunk}";
            var tenant = await _tenants.GetAsync(listen.TenantId.Value, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(tenant?.Options.OutboundCallerId))
                action.CallerId = tenant.Options.OutboundCallerId;
        }

        // The agent's leg, by uniqueid (ChanSpy option u). No VERBARA_OUTBOUND_ID: the bridge never tracks this leg.
        action.SetVariable(SpyTargetVariable, agentLeg);

        _legControl.EnsureObserving();
        OriginateResult result;
        try
        {
            result = await _originate.ExecuteAsync(action, PrimaryNode, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOriginateFailed(listen.ListenId, ex.Message);
            return SupervisorListenRefusal.OriginateFailed;
        }

        if (result.Success)
            return null;
        LogOriginateFailed(listen.ListenId, result.ErrorMessage ?? "originate refused");
        return SupervisorListenRefusal.OriginateFailed;
    }

    /// <summary>The trunk exactly as click-to-dial resolves it: the optional outbound route, then the default trunk.</summary>
    private async Task<string?> ResolveTrunkAsync(TenantId tenantId, string number, CancellationToken ct)
    {
        string? trunk = null;
        if (_routes is not null)
        {
            try
            {
                trunk = (await _routes.ResolveAsync(tenantId.Value, number, campaignId: null, ct).ConfigureAwait(false))?.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRouteFailed(ex.Message);
            }
        }

        return trunk ?? _defaultTrunk;
    }

    private static SupervisorListenDto ToDto(ActiveListen listen) =>
        new(listen.ListenId, listen.ConversationId, listen.Destination, listen.State, listen.StartedAt, listen.ListeningSince);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LISTEN] Listen {ListenId} started on conversation {ConversationId} ({Destination}) in tenant {TenantId}")]
    private partial void LogListenStarted(string listenId, string conversationId, string destination, string tenantId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[LISTEN] Listen {ListenId} stopped on conversation {ConversationId} in tenant {TenantId}")]
    private partial void LogListenStopped(string listenId, string conversationId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[LISTEN] Originate for listen {ListenId} failed: {Reason}")]
    private partial void LogOriginateFailed(string listenId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[LISTEN] Outbound route resolve for a phone listen failed (falling back to the default trunk): {Reason}")]
    private partial void LogRouteFailed(string reason);
}
