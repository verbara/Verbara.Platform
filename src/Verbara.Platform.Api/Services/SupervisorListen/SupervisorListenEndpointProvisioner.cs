using System.Security.Cryptography;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Services.SupervisorListen;

/// <summary>
/// supervisor-silent-listen (design D3): provisions a supervisor's receive-only listen endpoint through the existing
/// desired-state path, with no Pro change. The endpoint is a reserved <c>sup-{userId}</c> slot (an agent id is 32 hex
/// characters, so it can never collide) on a per-tenant <c>supervisor-listen</c> endpoint profile: a copy of the
/// tenant's default agent profile with the dead-end context <c>listen-only</c> and one contact. It is never an
/// <c>agents</c> row, so never a licensed agent.
/// <para>
/// Credentials are minted once, with <see cref="RandomNumberGenerator"/>, and returned only to their owner. Fail-closed:
/// without the realtime profile store there is no listen-only profile, and an endpoint without it would inherit the
/// tenant's default, dialable context, so nothing is minted.
/// </para>
/// </summary>
internal sealed partial class SupervisorListenEndpointProvisioner(
    ISupervisorListenProfileStore profiles,
    IUserStore users,
    IRealtimeSyncService sync,
    EndpointProfileStoreBase? endpointProfiles,
    IClock clock,
    ILogger<SupervisorListenEndpointProvisioner> logger)
{
    /// <summary>The per-tenant endpoint profile every listen endpoint uses.</summary>
    public const string ProfileName = "supervisor-listen";

    /// <summary>The dead-end dialplan context of the listen profile (rendered <c>{tenant}-listen-only</c>; no dialplan defines it).</summary>
    public const string ListenOnlyContext = "listen-only";

    /// <summary><c>ps_auths.username</c> is <c>VARCHAR(40)</c>.</summary>
    public const int MaxAuthUsernameLength = 40;

    private const string ExtensionPrefix = "sl";
    private const int MintAttempts = 5;

    /// <summary>The reserved desired-state slot of a supervisor's listen endpoint.</summary>
    public static string Slot(EntityId userId) => $"sup-{userId.Value}";

    /// <summary>The digest username of a listen endpoint: <c>{tenantId}-{extension}</c>.</summary>
    public static string AuthUsername(TenantId tenantId, string extension) => $"{tenantId.Value}-{extension}";

    /// <summary>Whether a tenant id leaves room for <c>{tenantId}-sl{8 hex}</c> in the 40-character SIP username.</summary>
    public static bool FitsSipUsername(TenantId tenantId) =>
        tenantId.Value.Length + 1 + ExtensionPrefix.Length + 8 <= MaxAuthUsernameLength;

    /// <summary>The id of the tenant's <c>supervisor-listen</c> endpoint profile, or null when there is none.</summary>
    public static async Task<long?> FindProfileIdAsync(EndpointProfileStoreBase? store, string tenantId, CancellationToken ct)
    {
        if (store is null)
            return null;
        var all = await store.ListAsync(tenantId, ct).ConfigureAwait(false);
        return all.FirstOrDefault(p => string.Equals(p.Name, ProfileName, StringComparison.Ordinal))?.Id;
    }

    /// <summary>
    /// Returns the caller's listen credentials, minting them on first use; <see langword="null"/> when the endpoint
    /// cannot be provisioned (<c>no-supervisor-endpoint</c>).
    /// </summary>
    public async Task<SupervisorListenEndpointDto?> ProvisionAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        if (!FitsSipUsername(tenantId) || endpointProfiles is null)
            return null;

        var profileId = await EnsureListenProfileAsync(tenantId, ct).ConfigureAwait(false);
        var user = await users.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false);
        var displayName = string.IsNullOrWhiteSpace(user?.DisplayName) ? userId.Value : user.DisplayName;

        var profile = await profiles.GetAsync(tenantId, userId, ct).ConfigureAwait(false);
        if (profile is null || !profile.HasCredentials)
        {
            profile = await MintAsync(tenantId, userId, profile, ct).ConfigureAwait(false);
            if (profile is null)
                return null;
            await TrySyncAsync(tenantId, userId, displayName, profile, profileId, ct).ConfigureAwait(false);
        }

        return new SupervisorListenEndpointDto(
            SupervisorListenService.EndpointId(tenantId, userId),
            AuthUsername(tenantId, profile.SipExtension!),
            profile.SipPassword!,
            displayName,
            SupervisorListenEndpointDto.CallerName);
    }

    private async Task<long> EnsureListenProfileAsync(TenantId tenantId, CancellationToken ct)
    {
        if (await FindProfileIdAsync(endpointProfiles, tenantId.Value, ct).ConfigureAwait(false) is { } existing)
            return existing;

        var template = await endpointProfiles!.GetDefaultAsync(tenantId.Value, EndpointProfileType.Agent, ct).ConfigureAwait(false)
            ?? new EndpointProfile();
        var listen = new EndpointProfile
        {
            TenantId = tenantId.Value,
            Name = ProfileName,
            Type = EndpointProfileType.Agent,
            IsDefault = false,
            Transport = template.Transport,
            Codecs = template.Codecs,
            Webrtc = template.Webrtc,
            MaxContacts = 1,
            DtlsAutoGenerateCert = template.DtlsAutoGenerateCert,
            DirectMedia = template.DirectMedia,
            Context = ListenOnlyContext,
            QualifyFrequency = template.QualifyFrequency,
        };
        return await endpointProfiles.CreateAsync(listen, tenantId.Value, ct).ConfigureAwait(false);
    }

    /// <summary>Mints an extension unique in the tenant and a password, keeping any stored listen phone.</summary>
    private async Task<SupervisorListenProfile?> MintAsync(
        TenantId tenantId, EntityId userId, SupervisorListenProfile? existing, CancellationToken ct)
    {
        var now = clock.UtcNow;
        for (var attempt = 0; attempt < MintAttempts; attempt++)
        {
            var minted = new SupervisorListenProfile
            {
                TenantId = tenantId,
                UserId = userId,
                SipExtension = ExtensionPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
                SipPassword = Base64Url(RandomNumberGenerator.GetBytes(24)),
                PhoneNumber = existing?.PhoneNumber,
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
            };
            if (await profiles.SaveAsync(minted, ct).ConfigureAwait(false))
                return minted;
        }

        LogExtensionExhausted(tenantId.Value);
        return null;
    }

    /// <summary>
    /// Best-effort immediate sync (the <c>RealtimeSyncingAgentStore</c> pattern), so the console can register at once
    /// instead of waiting for the next reconcile, which converges anyway.
    /// </summary>
    private async Task TrySyncAsync(
        TenantId tenantId, EntityId userId, string displayName, SupervisorListenProfile profile, long profileId, CancellationToken ct)
    {
        try
        {
            await sync.SyncAgentAsync(tenantId.Value, Slot(userId), displayName, profile.SipExtension!, profile.SipPassword!,
                profileId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSyncDeferred(tenantId.Value, ex.Message);
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[LISTEN] The listen endpoint of a supervisor in tenant {TenantId} was saved but its immediate realtime sync failed; the next reconcile converges it: {Reason}")]
    private partial void LogSyncDeferred(string tenantId, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[LISTEN] No free listen extension was found in tenant {TenantId} after several attempts.")]
    private partial void LogExtensionExhausted(string tenantId);
}
