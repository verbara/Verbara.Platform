using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>Where a live voice call is being blind-transferred (3B.2c). External is added in 3B.2d.</summary>
internal enum VoiceTransferKind
{
    Queue,
    Agent,
}

/// <summary>A blind-transfer destination: <see cref="Value"/> is the queue id or agent id.</summary>
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
    /// <summary>Fixed extension in both <c>[stasis-queue]</c> and <c>[transfer-agent]</c>.</summary>
    private const string FixedExten = "s";

    private readonly IConversationStore _conversations;
    private readonly IQueueStore _queues;
    private readonly IAgentStore _agents;
    private readonly VerbaraServerPool _serverPool;
    private readonly IClusterLeader _leader;
    private readonly ILogger<VoiceCallControlService> _logger;

    public VoiceCallControlService(
        IConversationStore conversations,
        IQueueStore queues,
        IAgentStore agents,
        VerbaraServerPool serverPool,
        [FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader leader,
        ILogger<VoiceCallControlService> logger)
    {
        _conversations = conversations;
        _queues = queues;
        _agents = agents;
        _serverPool = serverPool;
        _leader = leader;
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

        // Resolve the destination, set the variable the target context reads, then Redirect the
        // customer leg. Queue → [stasis-queue] (the inbound contract: QUEUE_NAME + exten s);
        // Agent → [transfer-agent] (dials TRANSFER_TARGET). AMI sends are best-effort (mirrors
        // RealtimeStateBridge): a stale channel just leaves the call as-is.
        string context;
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
            default:
                return new VoiceTransferOutcome(false, "unsupported-target");
        }

        await server.Connection.SendActionAsync(
            new RedirectAction { Channel = channel, Context = context, Exten = FixedExten, Priority = 1 },
            ct).ConfigureAwait(false);

        LogTransfer(conversationId.Value, target.Kind, target.Value);
        return new VoiceTransferOutcome(true, null);
    }

    private static async Task SendVarAsync(VerbaraServer server, string channel, string variable, string value, CancellationToken ct) =>
        await server.Connection.SendActionAsync(
            new SetVarAction { Channel = channel, Variable = variable, Value = value }, ct).ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[VOICE-XFER] Conversation {ConversationId} blind-transferred to {Kind} {Target}")]
    private partial void LogTransfer(string conversationId, VoiceTransferKind kind, string target);
}
