using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// Wraps the in-memory <see cref="InMemoryAgentCapacityService"/> with write-through persistence
/// via <see cref="IAgentCapacityStore"/>. On first use, reconciles in-memory state against
/// active conversations to recover from server restarts.
/// </summary>
public sealed class PersistentAgentCapacityService : IAgentCapacityService
{
    private readonly InMemoryAgentCapacityService _inner;
    private readonly IAgentCapacityStore _store;
    private readonly IConversationStore _conversationStore;
    private readonly IAgentStore _agentStore;

    private int _reconciled;

    public PersistentAgentCapacityService(
        IAgentStore agentStore,
        IAgentCapacityStore store,
        IConversationStore conversationStore)
    {
        _agentStore = agentStore;
        _inner = new InMemoryAgentCapacityService(agentStore);
        _store = store;
        _conversationStore = conversationStore;
    }

    public async Task<bool> HasCapacityAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        await EnsureReconciledAsync(tenantId, ct).ConfigureAwait(false);
        return await _inner.HasCapacityAsync(tenantId, agentId, channel, ct).ConfigureAwait(false);
    }

    public async Task ReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        await EnsureReconciledAsync(tenantId, ct).ConfigureAwait(false);
        await _inner.ReserveAsync(tenantId, agentId, channel, ct).ConfigureAwait(false);
        await PersistCurrentLoadsAsync(tenantId, agentId, ct).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        await EnsureReconciledAsync(tenantId, ct).ConfigureAwait(false);
        await _inner.ReleaseAsync(tenantId, agentId, channel, ct).ConfigureAwait(false);
        await PersistCurrentLoadsAsync(tenantId, agentId, ct).ConfigureAwait(false);
    }

    public async Task<int> GetCurrentLoadAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        await EnsureReconciledAsync(tenantId, ct).ConfigureAwait(false);
        return await _inner.GetCurrentLoadAsync(tenantId, agentId, channel, ct).ConfigureAwait(false);
    }

    private async Task EnsureReconciledAsync(TenantId tenantId, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _reconciled, 1, 0) == 0)
        {
            await ReconcileAsync(tenantId, ct).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileAsync(TenantId tenantId, CancellationToken ct)
    {
        // Load all active conversations and count per agent per channel
        var activeConversations = await _conversationStore
            .ListByStateAsync(tenantId, ConversationState.Active, 10_000, ct)
            .ConfigureAwait(false);

        var loads = new Dictionary<(string AgentId, ChannelType Channel), int>();

        foreach (var conv in activeConversations)
        {
            if (conv.Owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } ownerId })
                continue;

            var key = (ownerId.Value, conv.Channel);
            loads[key] = loads.GetValueOrDefault(key) + 1;
        }

        // Set in-memory capacity to match actual ownership
        foreach (var ((agentIdStr, channel), count) in loads)
        {
            var agentId = EntityId.From(agentIdStr);
            for (var i = 0; i < count; i++)
            {
                await _inner.ReserveAsync(tenantId, agentId, channel, ct).ConfigureAwait(false);
            }
        }

        // Persist reconciled values to store
        var agentRecords = new Dictionary<string, AgentCapacityRecord>();
        foreach (var ((agentIdStr, channel), count) in loads)
        {
            if (!agentRecords.TryGetValue(agentIdStr, out var record))
            {
                record = new AgentCapacityRecord
                {
                    TenantId = tenantId.Value,
                    AgentId = agentIdStr,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                agentRecords[agentIdStr] = record;
            }

            switch (channel)
            {
                case ChannelType.Voice:
                    record.VoiceLoad = count;
                    break;
                case ChannelType.WhatsApp:
                case ChannelType.WebChat:
                case ChannelType.Messenger:
                case ChannelType.Instagram:
                case ChannelType.Telegram:
                case ChannelType.Twitter:
                case ChannelType.Video:
                case ChannelType.Rcs:
                    record.ChatLoad += count;
                    break;
                case ChannelType.Email:
                    record.EmailLoad = count;
                    break;
                case ChannelType.Sms:
                    record.SmsLoad = count;
                    break;
            }
        }

        foreach (var record in agentRecords.Values)
        {
            await _store.UpsertAsync(record, ct).ConfigureAwait(false);
        }
    }

    private async Task PersistCurrentLoadsAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var voice = await _inner.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Voice, ct).ConfigureAwait(false);
        var chat = await _inner.GetCurrentLoadAsync(tenantId, agentId, ChannelType.WebChat, ct).ConfigureAwait(false);
        var email = await _inner.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Email, ct).ConfigureAwait(false);
        var sms = await _inner.GetCurrentLoadAsync(tenantId, agentId, ChannelType.Sms, ct).ConfigureAwait(false);

        await _store.UpsertAsync(new AgentCapacityRecord
        {
            TenantId = tenantId.Value,
            AgentId = agentId.Value,
            VoiceLoad = voice,
            ChatLoad = chat,
            EmailLoad = email,
            SmsLoad = sms,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }
}
