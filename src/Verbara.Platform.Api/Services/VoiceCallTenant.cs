using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The fail-closed read of a live call's tenant off its trunk leg: the <c>TENANT_ID</c> channel variable (set by the
/// trunk's <c>ps_endpoints set_var</c>, Phase 2.4), read with an AMI <c>GetVar</c> through the primary server and
/// stamped onto <see cref="CallSession.TenantId"/>. Shared by <see cref="VoiceConversationBridge"/> (its queue and
/// connection handlers, and its stamp on lease acquisition) and <see cref="VoiceCallTenantResolver"/>
/// (call-records-are-written-once design D5, round 12 F1), so both read the tenant the same way.
/// </summary>
internal static partial class VoiceCallTenant
{
    private const string TenantVariable = "TENANT_ID";

    /// <summary>Whether <paramref name="session"/> is still up: an ended call's channels are gone, so nothing is read.</summary>
    internal static bool IsLive(CallSession session) =>
        session.State is not (CallSessionState.Completed or CallSessionState.Failed or CallSessionState.TimedOut);

    /// <summary>
    /// Returns the session's tenant, reading and stamping it on first resolution from the trunk leg (the caller leg, or
    /// the first participant). <see langword="null"/> (fail-closed) when the session has no participant, AMI is
    /// unavailable, the action fails or the variable is unset.
    /// </summary>
    internal static async Task<string?> ResolveAsync(VerbaraServerPool serverPool, CallSession session, ILogger logger)
    {
        if (!string.IsNullOrEmpty(session.TenantId))
            return session.TenantId;

        var trunk = session.Participants.FirstOrDefault(p => p.Role == ParticipantRole.Caller)
                    ?? (session.Participants.Count > 0 ? session.Participants[0] : null);
        if (trunk is null)
            return null;

        return await ReadAndStampAsync(serverPool, session, trunk.Channel, logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>TENANT_ID</c> on <paramref name="channel"/> via an AMI GetVar and, on success, stamps it onto
    /// <see cref="CallSession.TenantId"/> (the Sdk's reserved hook). Returns <see langword="null"/> (fail-closed) when AMI
    /// is unavailable, the variable is unset, or the action fails.
    /// </summary>
    internal static async Task<string?> ReadAndStampAsync(
        VerbaraServerPool serverPool, CallSession session, string channel, ILogger logger)
    {
        var server = serverPool.GetServer(AsteriskNodeIds.Primary);
        if (server is null)
            return null; // AMI not connected / unconfigured — fail-closed

        try
        {
            var response = await server.Connection.SendActionAsync<GetVarResponse>(
                new GetVarAction { Channel = channel, Variable = TenantVariable },
                CancellationToken.None).ConfigureAwait(false);

            var ok = string.Equals(response.Response, "Success", StringComparison.OrdinalIgnoreCase);
            if (!ok || string.IsNullOrEmpty(response.Value))
                return null;

            // Stamp the Sdk's reserved tenant hook so the other observers on this (leader) pod — the event store,
            // AgentAssist — resolve the same tenant; followers never stamp, so their fail-closed paths keep the side
            // effects single cluster-wide.
            session.TenantId = response.Value;
            return response.Value;
        }
        catch (Exception ex)
        {
            LogGetVarFailed(logger, channel, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[VOICE-CONV] AMI GetVar TENANT_ID failed on channel {Channel}: {Reason}")]
    private static partial void LogGetVarFailed(ILogger logger, string channel, string reason);
}
