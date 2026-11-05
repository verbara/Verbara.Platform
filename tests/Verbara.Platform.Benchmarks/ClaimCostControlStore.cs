// conversations-close-after-inactivity task 2.9, negative control (B only, CLAIM_COST_CONTROL=true in the environment): the conversation
// store is wrapped so every claim UPDATE (inbound claim, send claim) runs twice. A bench that shows no added bytes per
// op with this wrapper cannot see the claim. Every other member forwards the inner store's task unchanged (no
// allocation of its own), so the added bytes are the second claim's (plus one async state machine per claim).
#if CLAIM_COST_CONTROL
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Benchmarks;

internal static partial class ClaimCostControl
{
    static partial void ApplyCore(IServiceCollection services)
    {
        var descriptor = services.Last(d => d.ServiceType == typeof(IConversationStore));
        services.Remove(descriptor);
        services.AddSingleton<IConversationStore>(sp =>
        {
            var inner = descriptor.ImplementationInstance as IConversationStore
                ?? descriptor.ImplementationFactory?.Invoke(sp) as IConversationStore
                ?? (IConversationStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            return new DoubleClaimConversationStore(inner);
        });
        Console.WriteLine("// ClaimCostControl: every claim UPDATE runs twice.");
    }
}

internal sealed class DoubleClaimConversationStore(IConversationStore inner) : IConversationStore
{
    public async Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct)
    {
        await inner.TryClaimForInboundAsync(tenantId, conversationId, now, ct).ConfigureAwait(false);
        return await inner.TryClaimForInboundAsync(tenantId, conversationId, now, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryClaimForSendAsync(
        TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct)
    {
        await inner.TryClaimForSendAsync(tenantId, conversationId, now, advanceClock, ct).ConfigureAwait(false);
        return await inner.TryClaimForSendAsync(tenantId, conversationId, now, advanceClock, ct).ConfigureAwait(false);
    }

    public Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
        inner.GetByIdAsync(tenantId, conversationId, ct);
    public Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct) =>
        inner.ListAsync(tenantId, query, ct);
    public Task SaveAsync(Conversation conversation, CancellationToken ct) => inner.SaveAsync(conversation, ct);
    public Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
        inner.FindActiveByContactAsync(tenantId, contactId, channel, ct);
    public Task<int> CountOpenByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
        inner.CountOpenByContactAsync(tenantId, contactId, channel, ct);
    public Task<DateTimeOffset?> FindLastInboundAtAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
        inner.FindLastInboundAtAsync(tenantId, contactId, channel, ct);
    public Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct) =>
        inner.FindByVoiceLinkedIdAsync(tenantId, voiceLinkedId, ct);
    public Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct) =>
        inner.FindByVoiceLinkedIdAcrossTenantsAsync(voiceLinkedId, ct);
    public Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct) =>
        inner.FindByIdAcrossTenantsAsync(conversationId, ct);
    public Task<PagedResult<ContactConversationSummary>> ListContactSummariesAsync(
        TenantId tenantId, EntityId contactId, EntityId? ownedByAgentId, int page, int pageSize, CancellationToken ct) =>
        inner.ListContactSummariesAsync(tenantId, contactId, ownedByAgentId, page, pageSize, ct);
    public Task<bool> HoldsOpenConversationOfContactAsync(TenantId tenantId, EntityId contactId, EntityId agentId, CancellationToken ct) =>
        inner.HoldsOpenConversationOfContactAsync(tenantId, contactId, agentId, ct);

    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        inner.ListByContactAsync(tenantId, contactId, ct);
    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        inner.DeleteByContactAsync(tenantId, contactId, ct);
    public Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) =>
        inner.DeleteIfNoMessagesAsync(tenantId, conversationId, ct);
    public Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct) =>
        inner.DeleteClosedBeforeAsync(tenantId, cutoff, ct);
    public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct) =>
        inner.ListQueuedAsync(tenantId, limit, ct);
    public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct) =>
        inner.ListByStateAsync(tenantId, state, limit, ct);
    public Task<bool> TryConditionalCloseWrapUpAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) =>
        inner.TryConditionalCloseWrapUpAsync(tenantId, conversationId, now, ct);
    public Task<bool> TryCommitWrapUpAsync(
        TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct) =>
        inner.TryCommitWrapUpAsync(tenantId, conversationId, ownerAgentId, now, ct);
    public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
        inner.CountActiveWorkAsync(tenantId, agentId, ct);
    public Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
        inner.ListFailoverWorkByOwnerAsync(tenantId, agentId, ct);
    public Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct) =>
        inner.ListPendingCallbackEvalAsync(ct);
    public Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct) =>
        inner.ListCallbackStuckAsync(tenantId, ct);
    public Task<IReadOnlyList<Conversation>> ListInactiveAsync(
        TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct) =>
        inner.ListInactiveAsync(tenantId, states, cutoff, limit, ct);
    public Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct) =>
        inner.TryCloseInactiveAsync(tenantId, request, ct);
    public Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) =>
        inner.TryCloseResolvedForReplyAsync(tenantId, conversationId, now, ct);
    public Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct) =>
        inner.ReconcileLastMessageAtAsync(tenantId, ct);
}
#endif
