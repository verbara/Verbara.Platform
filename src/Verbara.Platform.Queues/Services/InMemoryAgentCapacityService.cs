using System.Collections.Concurrent;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

public sealed class InMemoryAgentCapacityService : IAgentCapacityService
{
    private readonly IAgentCapacityResolver _resolver;
    private readonly ConcurrentDictionary<(TenantId, EntityId, ChannelType), int> _load = new();

    // Serialises every load write with TryReserveAsync's check, so the check and its increment are one step.
    private readonly Lock _gate = new();

    public InMemoryAgentCapacityService(IAgentCapacityResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <summary>
    /// W6 — collapses every chat-family sub-channel (WhatsApp/WebChat/Messenger/Instagram/
    /// Telegram/Twitter/Video/Rcs) onto the single canonical <see cref="ChannelType.WebChat"/>
    /// bucket so the whole family shares ONE capacity slot vs MaxChat. Voice/Email/Sms map to
    /// themselves. Any unrecognized future channel maps to itself (an unmapped GetMax returns 0,
    /// failing closed) rather than silently pooling into chat.
    /// </summary>
    private static ChannelType NormalizeToCapacityBucket(ChannelType channel) => channel switch
    {
        ChannelType.WhatsApp => ChannelType.WebChat,
        ChannelType.WebChat => ChannelType.WebChat,
        ChannelType.Messenger => ChannelType.WebChat,
        ChannelType.Instagram => ChannelType.WebChat,
        ChannelType.Telegram => ChannelType.WebChat,
        ChannelType.Twitter => ChannelType.WebChat,
        ChannelType.Video => ChannelType.WebChat,
        ChannelType.Rcs => ChannelType.WebChat,
        ChannelType.Voice => ChannelType.Voice,
        ChannelType.Email => ChannelType.Email,
        ChannelType.Sms => ChannelType.Sms,
        _ => channel,
    };

    /// <summary>
    /// W6 — the SUM of ASYNC load (pooled chat + email + sms). Voice is an exclusive lane and is
    /// EXCLUDED on purpose. Reads the in-memory ledger directly (synchronous; the dict is local).
    /// </summary>
    private int ComputeAsyncTotalLoad(TenantId tenantId, EntityId agentId)
    {
        var chat = _load.GetValueOrDefault((tenantId, agentId, ChannelType.WebChat), 0);
        var email = _load.GetValueOrDefault((tenantId, agentId, ChannelType.Email), 0);
        var sms = _load.GetValueOrDefault((tenantId, agentId, ChannelType.Sms), 0);
        return chat + email + sms;
    }

    public async Task<bool> HasCapacityAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        // W6 — a SINGLE agent read, owned by the resolver: it returns the EFFECTIVE capacity for
        // a present agent or null for a deleted/phantom one. Failing closed on null keeps work
        // from ever routing to an agent that no longer exists, with no second store round-trip.
        var effective = await _resolver.ResolveAsync(tenantId, agentId, ct).ConfigureAwait(false);
        lock (_gate)
            return Fits(effective, tenantId, agentId, NormalizeToCapacityBucket(channel));
    }

    public async Task<bool> TryReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var effective = await _resolver.ResolveAsync(tenantId, agentId, ct).ConfigureAwait(false);
        var bucket = NormalizeToCapacityBucket(channel);
        // The check and the increment are one step under the gate every load write takes (design D14).
        lock (_gate)
        {
            if (!Fits(effective, tenantId, agentId, bucket))
                return false;
            _load[(tenantId, agentId, bucket)] = _load.GetValueOrDefault((tenantId, agentId, bucket), 0) + 1;
            return true;
        }
    }

    // Whether one more unit of `bucket` fits the agent's effective capacity. Callers hold _gate.
    private bool Fits(ChannelCapacity? effective, TenantId tenantId, EntityId agentId, ChannelType bucket)
    {
        if (effective is null)
            return false;

        if (bucket == ChannelType.Voice)
        {
            // Voice is the EXCLUSIVE lane — gated only by MaxVoice (pinned 1 by the resolver),
            // and it does NOT count toward MaxTotal.
            return _load.GetValueOrDefault((tenantId, agentId, ChannelType.Voice), 0) < effective.MaxVoice;
        }

        // bucket is canonical here (WebChat/Email/Sms) → GetMax maps chat→MaxChat, email→MaxEmail,
        // sms→MaxSms. Any other bucket returns 0 and fails closed.
        var perChannelMax = effective.GetMax(bucket);
        if (perChannelMax <= 0)
            return false;

        var channelLoad = _load.GetValueOrDefault((tenantId, agentId, bucket), 0);
        if (channelLoad >= perChannelMax)
            return false;

        // W6 — async channels additionally share the MaxTotal cap across {chat-pool + email + sms}.
        var asyncTotal = ComputeAsyncTotalLoad(tenantId, agentId);
        return asyncTotal < effective.MaxTotal;
    }

    public Task ReserveAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var bucket = NormalizeToCapacityBucket(channel);
        lock (_gate)
            _load.AddOrUpdate((tenantId, agentId, bucket), 1, static (_, current) => current + 1);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var bucket = NormalizeToCapacityBucket(channel);
        lock (_gate)
        {
            _load.AddOrUpdate(
                (tenantId, agentId, bucket),
                0,
                static (_, current) => current > 0 ? current - 1 : 0);
        }
        return Task.CompletedTask;
    }

    public Task<int> GetCurrentLoadAsync(TenantId tenantId, EntityId agentId, ChannelType channel, CancellationToken ct)
    {
        var bucket = NormalizeToCapacityBucket(channel);
        var current = _load.GetValueOrDefault((tenantId, agentId, bucket), 0);
        return Task.FromResult(current);
    }
}
