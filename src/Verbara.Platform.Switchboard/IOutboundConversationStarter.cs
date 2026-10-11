using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// Starts an outbound digital conversation in the calling agent's own name (agent-started-conversations,
/// design D14): the first message is sent before anything is persisted, and the conversation is born
/// <see cref="ConversationState.Active"/> and owned by the agent. It is persisted through the conversation store and
/// writes no licensed-agent ledger row.
/// </summary>
public interface IOutboundConversationStarter
{
    Task<OutboundStartOutcome> StartAsync(OutboundStartRequest request, CancellationToken ct);
}

/// <summary>What an agent asks for when it starts a conversation with a contact.</summary>
/// <param name="TenantId">The tenant of the agent and the contact.</param>
/// <param name="ContactId">The contact to write to.</param>
/// <param name="Channel">The digital channel to start on.</param>
/// <param name="AgentId">The calling agent's own Agent profile: the owner-to-be.</param>
/// <param name="ActorUserId">The calling user, recorded as the creator of the conversation and its message.</param>
/// <param name="Content">The initial message.</param>
/// <param name="TemplateName">The provider template to send instead of free-form text, if any.</param>
/// <param name="TemplateLanguage">The template's language code, if any.</param>
public sealed record OutboundStartRequest(
    TenantId TenantId,
    EntityId ContactId,
    ChannelType Channel,
    EntityId AgentId,
    string ActorUserId,
    MessageEnvelope Content,
    string? TemplateName = null,
    string? TemplateLanguage = null);

/// <summary>Stable machine codes of a refused start (design D14).</summary>
public static class OutboundStartCodes
{
    /// <summary>The contact has no address on the channel (400).</summary>
    public const string ContactHasNoAddress = "contact-has-no-address";

    /// <summary>The channel has no registered outbound connector (409).</summary>
    public const string ChannelNotAvailable = "channel-not-available";

    /// <summary>An open conversation with the contact is owned by someone else, or queued unowned (409).</summary>
    public const string ConversationOpenElsewhere = "conversation-open-elsewhere";

    /// <summary>
    /// The caller owns the contact's open conversation, but in a state no message can be sent in — wrap-up,
    /// snoozed, waiting for the customer, resolved or escalated (409). It names that conversation; the agent resumes it.
    /// </summary>
    public const string ConversationNotSendable = "conversation-not-sendable";

    /// <summary>The calling agent has no capacity left on the channel (409).</summary>
    public const string AgentAtCapacity = "agent-at-capacity";

    /// <summary>The WhatsApp window is closed and no template was named (409).</summary>
    public const string WhatsAppTemplateRequired = WhatsAppWindowDecision.TemplateRequiredCode;

    /// <summary>The provider rejected the first message; nothing was persisted (502).</summary>
    public const string ChannelSendFailed = "channel-send-failed";

    /// <summary>
    /// The provider accepted the first message, but storing it failed (500). The contact has received it; the
    /// conversation does not exist and no capacity stays reserved. The provider message id is logged for
    /// reconciliation.
    /// </summary>
    public const string StartPersistFailed = "start-persist-failed";
}

/// <summary>
/// The typed outcome of <see cref="IOutboundConversationStarter.StartAsync"/>: either the started (or reused)
/// conversation with its sent message, or a refusal whose <see cref="RefusalCode"/> the API maps to a status.
/// </summary>
public sealed record OutboundStartOutcome
{
    private OutboundStartOutcome(
        Conversation? conversation,
        Message? message,
        bool reused,
        string? refusalCode,
        EntityId? openConversationId,
        ConversationOwnerKind? openOwnerKind)
    {
        Conversation = conversation;
        Message = message;
        Reused = reused;
        RefusalCode = refusalCode;
        OpenConversationId = openConversationId;
        OpenOwnerKind = openOwnerKind;
    }

    /// <summary>The started or reused conversation; <c>null</c> when refused.</summary>
    public Conversation? Conversation { get; }

    /// <summary>The initial message as stored; <c>null</c> when refused.</summary>
    public Message? Message { get; }

    /// <summary>True when an open conversation the caller already owned was reused.</summary>
    public bool Reused { get; }

    /// <summary>The refusal code (<see cref="OutboundStartCodes"/>); <c>null</c> on success.</summary>
    public string? RefusalCode { get; }

    /// <summary>For <see cref="OutboundStartCodes.ConversationOpenElsewhere"/> and <see cref="OutboundStartCodes.ConversationNotSendable"/>: the open conversation's id.</summary>
    public EntityId? OpenConversationId { get; }

    /// <summary>With <see cref="OpenConversationId"/>: the open conversation's owner kind; <c>null</c> when queued unowned.</summary>
    public ConversationOwnerKind? OpenOwnerKind { get; }

    /// <summary>True when the start did not produce a conversation (for <see cref="OutboundStartCodes.StartPersistFailed"/> the message was sent).</summary>
    public bool IsRefused => RefusalCode is not null;

    /// <summary>A started (or reused) conversation and its sent message.</summary>
    public static OutboundStartOutcome Started(Conversation conversation, Message message, bool reused)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);
        return new OutboundStartOutcome(conversation, message, reused, null, null, null);
    }

    /// <summary>A refusal with the stable <paramref name="code"/>.</summary>
    public static OutboundStartOutcome Refused(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new OutboundStartOutcome(null, null, false, code, null, null);
    }

    /// <summary>A refusal (<paramref name="code"/>) that names the contact's open conversation and its owner kind.</summary>
    public static OutboundStartOutcome NamingOpen(string code, EntityId conversationId, ConversationOwnerKind? ownerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new(null, null, false, code, conversationId, ownerKind);
    }
}
