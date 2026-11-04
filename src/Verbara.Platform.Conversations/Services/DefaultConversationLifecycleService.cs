using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations.Services;

public sealed class DefaultConversationLifecycleService : IConversationLifecycleService
{
    private readonly IConversationStore _conversationStore;
    private readonly IClock _clock;

    public DefaultConversationLifecycleService(IConversationStore conversationStore, IClock clock)
    {
        _conversationStore = conversationStore;
        _clock = clock;
    }

    public async Task<Conversation> CreateAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct)
    {
        var conversation = NewQueued(tenantId, contactId, channel);

        await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        return conversation;
    }

    private Conversation NewQueued(TenantId tenantId, EntityId contactId, ChannelType channel) =>
        new()
        {
            ConversationId = EntityId.New(),
            TenantId = tenantId,
            ContactId = contactId,
            Channel = channel,
            State = ConversationState.Queued,
            CreatedAt = _clock.UtcNow,
        };

    public async Task<Conversation> CreateForWebChatSessionAsync(TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionAddress);
        var conversation = NewQueued(tenantId, contactId, ChannelType.WebChat);
        conversation.SetMetadata(WebChatConversationMetadata.SessionAddressKey, sessionAddress.Address);

        await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
        return conversation;
    }

    public async Task TransitionAsync(TenantId tenantId, EntityId conversationId, ConversationState newState, CancellationToken ct)
    {
        var conversation = await _conversationStore.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found for tenant '{tenantId}'.");

        conversation.TransitionTo(newState, _clock.UtcNow);
        conversation.UpdatedAt = _clock.UtcNow;

        await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
    }

    public async Task CloseAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        var conversation = await _conversationStore.GetByIdAsync(tenantId, conversationId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation '{conversationId}' not found for tenant '{tenantId}'.");

        conversation.TransitionTo(ConversationState.Closed, _clock.UtcNow);
        conversation.ClosedAt ??= _clock.UtcNow;
        conversation.UpdatedAt = _clock.UtcNow;

        await _conversationStore.SaveAsync(conversation, ct).ConfigureAwait(false);
    }
}
