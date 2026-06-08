using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Bot;

/// <summary>
/// Processes inbound messages on behalf of a virtual agent and produces a <see cref="BotResponse"/>.
/// </summary>
public interface IVirtualAgent
{
    /// <summary>
    /// Processes an inbound message within the given conversation and returns the bot's response.
    /// </summary>
    Task<BotResponse> ProcessMessageAsync(
        EntityId conversationId,
        TenantId tenantId,
        MessageEnvelope message,
        CancellationToken ct);
}

/// <summary>
/// The outcome produced by a single <see cref="IVirtualAgent.ProcessMessageAsync"/> call.
/// </summary>
/// <param name="Action">What the bot decided to do.</param>
/// <param name="Messages">Outbound messages to send to the contact (populated when Action is <see cref="BotResponseAction.Reply"/>).</param>
/// <param name="TargetQueueId">Destination queue (populated when Action is <see cref="BotResponseAction.TransferToQueue"/>).</param>
/// <param name="Priority">Priority for the handoff (populated alongside <paramref name="TargetQueueId"/>).</param>
/// <param name="HandoffReason">Human-readable reason for a handoff (optional).</param>
/// <param name="FlowMetadata">
/// Non-private flow variables captured during bot execution (keys not prefixed with <c>__</c>),
/// carried through so downstream consumers (e.g. wrap-up pre-fill) can read them. May be empty.
/// </param>
public sealed record BotResponse(
    BotResponseAction Action,
    IReadOnlyList<MessageEnvelope>? Messages,
    EntityId? TargetQueueId,
    MessagePriority? Priority,
    string? HandoffReason,
    IReadOnlyDictionary<string, string>? FlowMetadata = null);

/// <summary>
/// Describes the action a bot has decided to take after processing a message.
/// </summary>
public enum BotResponseAction
{
    /// <summary>The bot is replying with one or more outbound messages.</summary>
    Reply = 0,

    /// <summary>The bot is handing the conversation off to a queue.</summary>
    TransferToQueue = 1,

    /// <summary>The bot considers the conversation resolved and is ending it.</summary>
    EndConversation = 2,
}
