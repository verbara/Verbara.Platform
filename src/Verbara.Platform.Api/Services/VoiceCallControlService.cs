using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>Where a live voice call is being blind-transferred (3B.2c queue/agent; 3B.2d external).</summary>
internal enum VoiceTransferKind
{
    Queue,
    Agent,
    External,
}

/// <summary>A blind-transfer destination: <see cref="Value"/> is the queue id, agent id, or external number.</summary>
internal sealed record VoiceTransferTarget(VoiceTransferKind Kind, string Value);

/// <summary>Result of a transfer attempt — <see cref="Error"/> is a stable machine code on failure.</summary>
internal sealed record VoiceTransferOutcome(bool Accepted, string? Error);

internal interface IVoiceCallControlService
{
    Task<VoiceTransferOutcome> BlindTransferAsync(
        TenantId tenantId, EntityId conversationId, VoiceTransferTarget target, CancellationToken ct);
}

/// <summary>
/// Server-orchestrated blind transfer of a live voice call (3B.2c). The browser softphone is a single
/// SIP.js session and cannot REFER, so the transfer is an AMI <c>Redirect</c> on the CUSTOMER (trunk)
/// leg — the channel the bridge persisted as <c>Metadata["customerChannel"]</c> at connect. Redirecting
/// that leg pulls the customer out of the agent bridge and re-enters the dialplan; the agent's leg then
/// hangs up (the 3B.1 wrap-up flow takes over). Leader-gated through the same <c>voice:ami:owner:leader</c>
/// lease as <see cref="VoiceConversationBridge"/> so only one pod emits the AMI command cluster-wide.
/// </summary>
internal sealed partial class VoiceCallControlService : IVoiceCallControlService
{
    /// <summary>Dialplan context that dials <c>${TRANSFER_TARGET}</c> (the resolved agent endpoint).</summary>
    private const string TransferAgentContext = "transfer-agent";
    /// <summary>Channel variable the <c>[transfer-agent]</c> context dials.</summary>
    private const string TransferTargetVariable = "TRANSFER_TARGET";
    /// <summary>Shared outbound context (3B.2d) — dials <c>PJSIP/${TRUNK}/${EXTEN}</c> with <c>${OUTBOUND_CALLERID}</c>.</summary>
    private const string OutboundAgentContext = "outbound-agent";
    private const string TrunkVariable = "TRUNK";
    private const string OutboundCallerIdVariable = "OUTBOUND_CALLERID";
    /// <summary>Fixed extension in both <c>[stasis-queue]</c> and <c>[transfer-agent]</c> (external uses the number).</summary>
    private const string FixedExten = "s";

    private readonly IConversationStore _conversations;
    private readonly IQueueStore _queues;
    private readonly IAgentStore _agents;
    private readonly VerbaraServerPool _serverPool;
    private readonly OutboundRouteResolverBase? _routes;
    private readonly ITenantStore _tenants;
    private readonly IClusterLeader _leader;
    private readonly string? _defaultTrunk;
    private readonly ILogger<VoiceCallControlService> _logger;

    public VoiceCallControlService(
        IConversationStore conversations,
        IQueueStore queues,
        IAgentStore agents,
        VerbaraServerPool serverPool,
        OutboundRouteResolverBase? routes,
        ITenantStore tenants,
        [FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader leader,
        string? defaultTrunk,
        ILogger<VoiceCallControlService> logger)
    {
        _conversations = conversations;
        _queues = queues;
        _agents = agents;
        _serverPool = serverPool;
        _routes = routes;
        _tenants = tenants;
        _leader = leader;
        _defaultTrunk = defaultTrunk;
        _logger = logger;
    }

    public async Task<VoiceTransferOutcome> BlindTransferAsync(
        TenantId tenantId, EntityId conversationId, VoiceTransferTarget target, CancellationToken ct)
    {
        // Only the AMI-owner leader emits AMI side-effects (mirrors VoiceConversationBridge).
        if (!_leader.IsLeader)
            return new VoiceTransferOutcome(false, "not-leader");

        var conversation = await _conversations.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false);
        if (conversation is null || conversation.Channel != ChannelType.Voice)
            return new VoiceTransferOutcome(false, "not-a-voice-conversation");

        if (!conversation.Metadata.TryGetValue("customerChannel", out var channel) || string.IsNullOrEmpty(channel))
            return new VoiceTransferOutcome(false, "channel-unknown");

        var server = _serverPool.GetServer("primary");
        if (server is null)
            return new VoiceTransferOutcome(false, "ami-unavailable");

        // Resolve the destination, set the variable(s) the target context reads, then Redirect the
        // customer leg. Queue → [stasis-queue] (the inbound contract: QUEUE_NAME + exten s);
        // Agent → [transfer-agent] (dials TRANSFER_TARGET); External → [outbound-agent] (TRUNK +
        // OUTBOUND_CALLERID, exten = the dialed number — shared with click-to-dial 3B.2d). AMI sends are
        // best-effort (mirrors RealtimeStateBridge): a stale channel just leaves the call as-is.
        string context;
        var exten = FixedExten;
        switch (target.Kind)
        {
            case VoiceTransferKind.Queue:
            {
                var queue = await _queues.GetByIdAsync(tenantId, EntityId.From(target.Value), ct).ConfigureAwait(false);
                if (queue is null)
                    return new VoiceTransferOutcome(false, "queue-not-found");
                await SendVarAsync(server, channel, StasisInboundConsumer.QueueNameVariable, $"{tenantId.Value}-{queue.Name}", ct).ConfigureAwait(false);
                context = StasisInboundConsumer.StasisQueueContext;
                break;
            }
            case VoiceTransferKind.Agent:
            {
                var agent = await _agents.GetByIdAsync(tenantId, EntityId.From(target.Value), ct).ConfigureAwait(false);
                if (agent is null)
                    return new VoiceTransferOutcome(false, "agent-not-found");
                await SendVarAsync(server, channel, TransferTargetVariable, $"PJSIP/{tenantId.Value}-agent-{agent.AgentId.Value}", ct).ConfigureAwait(false);
                context = TransferAgentContext;
                break;
            }
            case VoiceTransferKind.External:
            {
                var number = NormalizeNumber(target.Value);
                if (string.IsNullOrEmpty(number))
                    return new VoiceTransferOutcome(false, "invalid-number");

                // Same route→trunk + tenant caller-ID resolution as click-to-dial (3B.2d.2): optional
                // route resolver, falling back to the configured default trunk; caller ID from the tenant
                // setting (empty → the dialplan clears CALLERID → the trunk default is presented).
                string? trunk = null;
                if (_routes is not null)
                {
                    try
                    {
                        trunk = (await _routes.ResolveAsync(tenantId.Value, number, campaignId: null, ct).ConfigureAwait(false))?.Name;
                    }
                    catch (Exception ex)
                    {
                        LogRouteFailed(number, ex.Message);
                    }
                }
                trunk ??= _defaultTrunk;
                if (string.IsNullOrWhiteSpace(trunk))
                    return new VoiceTransferOutcome(false, "no-trunk");

                var tenant = await _tenants.GetAsync(tenantId.Value, ct).ConfigureAwait(false);
                var callerId = tenant?.Options.OutboundCallerId ?? "";
                await SendVarAsync(server, channel, TrunkVariable, trunk!, ct).ConfigureAwait(false);
                await SendVarAsync(server, channel, OutboundCallerIdVariable, callerId, ct).ConfigureAwait(false);
                context = OutboundAgentContext;
                exten = number;
                break;
            }
            default:
                return new VoiceTransferOutcome(false, "unsupported-target");
        }

        await server.Connection.SendActionAsync(
            new RedirectAction { Channel = channel, Context = context, Exten = exten, Priority = 1 },
            ct).ConfigureAwait(false);

        LogTransfer(conversationId.Value, target.Kind, target.Value);
        return new VoiceTransferOutcome(true, null);
    }

    private static async Task SendVarAsync(VerbaraServer server, string channel, string variable, string value, CancellationToken ct) =>
        await server.Connection.SendActionAsync(
            new SetVarAction { Channel = channel, Variable = variable, Value = value }, ct).ConfigureAwait(false);

    /// <summary>Keeps digits and a leading <c>+</c> only — strips spaces, dashes, and parentheses.</summary>
    private static string NormalizeNumber(string raw) =>
        new([.. raw.Where(c => char.IsDigit(c) || c == '+')]);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[VOICE-XFER] Conversation {ConversationId} blind-transferred to {Kind} {Target}")]
    private partial void LogTransfer(string conversationId, VoiceTransferKind kind, string target);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-XFER] Outbound route resolve for external {Number} failed (falling back to default trunk): {Reason}")]
    private partial void LogRouteFailed(string number, string reason);
}
