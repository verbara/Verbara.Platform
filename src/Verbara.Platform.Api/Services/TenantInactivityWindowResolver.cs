using Microsoft.Extensions.Options;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The host's <see cref="IInactivityWindowResolver"/>: the tenant's stored override through
/// <see cref="ITenantAuthConfigStore"/> (the cached store in the host) when it is at least
/// <see cref="ConversationOptionsSetup.MinAfterHours"/>, else <see cref="ConversationOptions.InactivityCloseAfterHours"/>.
/// No row, a null column or no store means the host window. Used by <see cref="ConversationTimeoutWorker"/> (which logs
/// the warnings) and by last-agent routing's clamp, so the two readings share one set of validity rules
/// (conversations-return-to-last-agent task 2.1).
/// </summary>
internal sealed class TenantInactivityWindowResolver : IInactivityWindowResolver
{
    private readonly ConversationOptions _options;
    private readonly ITenantAuthConfigStore? _tenantAuthConfigStore;

    public TenantInactivityWindowResolver(IOptions<ConversationOptions> options, ITenantAuthConfigStore? tenantAuthConfigStore = null)
        : this(options.Value, tenantAuthConfigStore)
    {
    }

    internal TenantInactivityWindowResolver(ConversationOptions options, ITenantAuthConfigStore? tenantAuthConfigStore)
    {
        _options = options;
        _tenantAuthConfigStore = tenantAuthConfigStore;
    }

    public async Task<InactivityWindowResolution> ResolveAsync(TenantId tenantId, CancellationToken ct)
    {
        var host = _options.InactivityCloseAfterHours;
        if (_tenantAuthConfigStore is null)
            return new InactivityWindowResolution(host, null, OverrideApplied: false);

        var stored = (await _tenantAuthConfigStore.GetAsync(tenantId.Value, ct).ConfigureAwait(false))?.InactivityCloseAfterHours;
        return stored switch
        {
            null => new InactivityWindowResolution(host, null, OverrideApplied: false),
            < ConversationOptionsSetup.MinAfterHours => new InactivityWindowResolution(host, stored, OverrideApplied: false),
            { } tenantHours => new InactivityWindowResolution(tenantHours, stored, OverrideApplied: true),
        };
    }
}
