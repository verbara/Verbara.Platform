using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// W6 — Api-layer <see cref="ICapacityDefaultsProvider"/> over <see cref="ITenantAuthConfigStore"/>.
/// Maps the per-tenant <c>Max*Default</c> columns to a <see cref="ChannelCapacity"/>. Lives in the
/// Api layer (not Queues) so Queues keeps no dependency on Identity. Reads hit the cached store
/// (AHH Phase 1), so the resolver inherits the auth hot-path cache for free.
/// </summary>
internal sealed class TenantAuthConfigCapacityDefaultsProvider : ICapacityDefaultsProvider
{
    private readonly ITenantAuthConfigStore _store;

    public TenantAuthConfigCapacityDefaultsProvider(ITenantAuthConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<ChannelCapacity> GetDefaultsAsync(TenantId tenantId, CancellationToken ct)
    {
        var cfg = await _store.GetAsync(tenantId.Value, ct).ConfigureAwait(false);
        if (cfg is null)
        {
            // Unconfigured tenant → the hard ChannelCapacity defaults (1/3/5/3/5).
            return new ChannelCapacity();
        }

        return new ChannelCapacity
        {
            MaxVoice = cfg.MaxVoiceDefault,
            MaxChat = cfg.MaxChatDefault,
            MaxEmail = cfg.MaxEmailDefault,
            MaxSms = cfg.MaxSmsDefault,
            MaxTotal = cfg.MaxTotalDefault,
        };
    }
}
