using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// Forwards every member to the real conversation store, with two test controls for the live-stream suites
/// (contact-history-opens-by-assignment task 1.9, design D17):
/// <list type="bullet">
/// <item><see cref="Hide"/>: <see cref="GetByIdAsync"/> answers <see langword="null"/> for a chosen conversation, the
/// "conversation cannot be loaded" case of a publisher, while the inbound pipeline (which never reads by id) still
/// stores its messages.</item>
/// <item><see cref="Detached"/>: every read returns a copy and every write stores a copy, as the Postgres store builds a
/// new object per read (spec audit F2). The in-memory store hands every caller the stored reference, which hides a
/// stamp taken from a stale pre-transfer object.</item>
/// </list>
/// </summary>
internal sealed class ProbeConversationStore(IConversationStore inner, bool detached) : IConversationStore
{
    private readonly ConcurrentDictionary<EntityId, byte> _hidden = new();
    private readonly ConcurrentDictionary<EntityId, Action<Conversation?, CancellationToken>> _onRead = new();

    public bool Detached { get; } = detached;

    /// <summary>From now on <see cref="GetByIdAsync"/> answers <see langword="null"/> for <paramref name="conversationId"/>.</summary>
    public void Hide(EntityId conversationId) => _hidden[conversationId] = 0;

    /// <summary>
    /// From now on <see cref="GetByIdAsync"/> of <paramref name="conversationId"/> runs <paramref name="onRead"/> on the row
    /// it read (and the token it was given) before answering; <paramref name="onRead"/> may throw, as a failed read does.
    /// </summary>
    public void OnRead(EntityId conversationId, Action<Conversation?, CancellationToken> onRead) =>
        _onRead[conversationId] = onRead;

    private Conversation? Out(Conversation? c) => Detached ? Copy(c) : c;

    private async Task<IReadOnlyList<Conversation>> OutAll(Task<IReadOnlyList<Conversation>> read)
    {
        var rows = await read;
        return Detached ? [.. rows.Select(r => Copy(r)!)] : rows;
    }

    public async Task<Conversation?> GetByIdAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        if (_hidden.ContainsKey(conversationId))
            return null;
        var row = Out(await inner.GetByIdAsync(tenantId, conversationId, ct));
        if (_onRead.TryGetValue(conversationId, out var onRead))
            onRead(row, ct);
        return row;
    }

    public async Task<PagedResult<Conversation>> ListAsync(TenantId tenantId, ConversationQuery query, CancellationToken ct)
    {
        var page = await inner.ListAsync(tenantId, query, ct);
        return Detached
            ? new PagedResult<Conversation>([.. page.Items.Select(c => Copy(c)!)], page.TotalCount, page.Page, page.PageSize)
            : page;
    }

    public Task SaveAsync(Conversation conversation, CancellationToken ct) =>
        inner.SaveAsync(Detached ? Copy(conversation)! : conversation, ct);

    public async Task<Conversation?> FindActiveByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) =>
        Out(await inner.FindActiveByContactAsync(tenantId, contactId, channel, ct));

    public async Task<Conversation?> FindByVoiceLinkedIdAsync(TenantId tenantId, string voiceLinkedId, CancellationToken ct) =>
        Out(await inner.FindByVoiceLinkedIdAsync(tenantId, voiceLinkedId, ct));

    public async Task<Conversation?> FindByVoiceLinkedIdAcrossTenantsAsync(string voiceLinkedId, CancellationToken ct) =>
        Out(await inner.FindByVoiceLinkedIdAcrossTenantsAsync(voiceLinkedId, ct));

    public async Task<Conversation?> FindByIdAcrossTenantsAsync(EntityId conversationId, CancellationToken ct) =>
        Out(await inner.FindByIdAcrossTenantsAsync(conversationId, ct));

    public Task<PagedResult<ContactConversationSummary>> ListContactSummariesAsync(
        TenantId tenantId, EntityId contactId, EntityId? ownedByAgentId, int page, int pageSize, CancellationToken ct) =>
        inner.ListContactSummariesAsync(tenantId, contactId, ownedByAgentId, page, pageSize, ct);
    public Task<EntityId?> FindLastAgentOwnerAsync(TenantId tenantId, EntityId contactId, DateTimeOffset since, CancellationToken ct) =>
        inner.FindLastAgentOwnerAsync(tenantId, contactId, since, ct);

    public Task<bool> HoldsOpenConversationOfContactAsync(TenantId tenantId, EntityId contactId, EntityId agentId, CancellationToken ct) =>
        inner.HoldsOpenConversationOfContactAsync(tenantId, contactId, agentId, ct);

    public Task<IReadOnlyList<Conversation>> ListByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) =>
        OutAll(inner.ListByContactAsync(tenantId, contactId, ct));

    public Task<IReadOnlyList<Conversation>> ListQueuedAsync(TenantId tenantId, int limit, CancellationToken ct) =>
        OutAll(inner.ListQueuedAsync(tenantId, limit, ct));

    public Task<IReadOnlyList<Conversation>> ListByStateAsync(TenantId tenantId, ConversationState state, int limit, CancellationToken ct) =>
        OutAll(inner.ListByStateAsync(tenantId, state, limit, ct));

    public Task<IReadOnlyList<Conversation>> ListFailoverWorkByOwnerAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) =>
        OutAll(inner.ListFailoverWorkByOwnerAsync(tenantId, agentId, ct));

    public Task<IReadOnlyList<Conversation>> ListPendingCallbackEvalAsync(CancellationToken ct) =>
        OutAll(inner.ListPendingCallbackEvalAsync(ct));

    public Task<IReadOnlyList<Conversation>> ListCallbackStuckAsync(TenantId tenantId, CancellationToken ct) =>
        OutAll(inner.ListCallbackStuckAsync(tenantId, ct));

    public Task<IReadOnlyList<Conversation>> ListInactiveAsync(TenantId tenantId, IReadOnlyCollection<ConversationState> states, DateTimeOffset cutoff, int limit, CancellationToken ct) =>
        OutAll(inner.ListInactiveAsync(tenantId, states, cutoff, limit, ct));

    public Task<Conversation?> FindOpenBySessionAddressAsync(TenantId tenantId, EntityId contactId, ChannelAddress sessionAddress, CancellationToken ct) => inner.FindOpenBySessionAddressAsync(tenantId, contactId, sessionAddress, ct);
    public Task<int> CountOpenByContactAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) => inner.CountOpenByContactAsync(tenantId, contactId, channel, ct);
    public Task<DateTimeOffset?> FindLastInboundAtAsync(TenantId tenantId, EntityId contactId, ChannelType channel, CancellationToken ct) => inner.FindLastInboundAtAsync(tenantId, contactId, channel, ct);
    public Task<int> DeleteByContactAsync(TenantId tenantId, EntityId contactId, CancellationToken ct) => inner.DeleteByContactAsync(tenantId, contactId, ct);
    public Task<bool> DeleteIfNoMessagesAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct) => inner.DeleteIfNoMessagesAsync(tenantId, conversationId, ct);
    public Task<int> DeleteClosedBeforeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken ct) => inner.DeleteClosedBeforeAsync(tenantId, cutoff, ct);
    public Task<bool> TryConditionalCloseWrapUpAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) => inner.TryConditionalCloseWrapUpAsync(tenantId, conversationId, now, ct);
    public Task<bool> TryCommitWrapUpAsync(TenantId tenantId, EntityId conversationId, EntityId ownerAgentId, DateTimeOffset now, CancellationToken ct) => inner.TryCommitWrapUpAsync(tenantId, conversationId, ownerAgentId, now, ct);
    public Task<int> CountActiveWorkAsync(TenantId tenantId, EntityId agentId, CancellationToken ct) => inner.CountActiveWorkAsync(tenantId, agentId, ct);
    public Task<InactivityCloseOutcome?> TryCloseInactiveAsync(TenantId tenantId, InactivityCloseRequest request, CancellationToken ct) => inner.TryCloseInactiveAsync(tenantId, request, ct);
    public Task<bool> TryClaimForInboundAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) => inner.TryClaimForInboundAsync(tenantId, conversationId, now, ct);
    public Task<bool> TryCloseResolvedForReplyAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, CancellationToken ct) => inner.TryCloseResolvedForReplyAsync(tenantId, conversationId, now, ct);
    public Task<bool> TryClaimForSendAsync(TenantId tenantId, EntityId conversationId, DateTimeOffset now, bool advanceClock, CancellationToken ct) => inner.TryClaimForSendAsync(tenantId, conversationId, now, advanceClock, ct);
    public Task<int> ReconcileLastMessageAtAsync(TenantId tenantId, CancellationToken ct) => inner.ReconcileLastMessageAtAsync(tenantId, ct);

    /// <summary>A copy that shares nothing mutable with <paramref name="c"/>, as a Postgres read builds one.</summary>
    internal static Conversation? Copy(Conversation? c)
    {
        if (c is null)
            return null;
        var copy = new Conversation
        {
            ConversationId = c.ConversationId,
            TenantId = c.TenantId,
            ContactId = c.ContactId,
            Channel = c.Channel,
            Owner = c.Owner,
            State = c.State,
            CaseId = c.CaseId,
            CreatedAt = c.CreatedAt,
            ClosedAt = c.ClosedAt,
            UpdatedAt = c.UpdatedAt,
            CreatedBy = c.CreatedBy,
            UpdatedBy = c.UpdatedBy,
            VoiceLinkedId = c.VoiceLinkedId,
            QueuePriority = c.QueuePriority,
            LastMessageAt = c.LastMessageAt,
            QueueId = c.QueueId,
        };
        foreach (var kv in c.Metadata)
            copy.SetMetadata(kv.Key, kv.Value);
        foreach (var session in c.Sessions)
            copy.AddSession(session);
        return copy;
    }
}
