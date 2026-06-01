using Verbara.Platform.Core;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Structured connectivity diagnostic for a single trunk. <see cref="Ok"/> is the overall verdict
/// (endpoint present AND, depending on <see cref="AuthMode"/>, registered / IP-ACL identify present).
/// The nullable booleans are tri-state: <c>null</c> means "not applicable for this auth mode" (e.g.
/// <see cref="Registered"/> is null for an IP-ACL trunk). <see cref="Messages"/> are operator-facing
/// (Spanish) lines — the same information the operator would read off <c>pjsip show ...</c> by hand.
/// </summary>
internal sealed record TrunkConnectivityResult(
    long TrunkId,
    string EndpointId,
    bool EndpointFound,
    string AuthMode,
    bool? Registered,
    bool? IdentifyPresent,
    bool? Reachable,
    bool Ok,
    IReadOnlyList<string> Messages);

internal interface ITrunkConnectivityTester
{
    /// <summary>The single message a not-found result carries — the endpoint maps it to HTTP 404.</summary>
    const string TrunkNotFoundMessage = "trunk no encontrado";

    Task<TrunkConnectivityResult> TestAsync(TenantId tenantId, long trunkId, CancellationToken ct);
}

/// <summary>
/// Runs the <c>pjsip show ...</c> CLI queries an operator would otherwise SSH for (see
/// <c>docs/manuales/smb/06-canal-voz-sip.md</c> §3.3) server-side over AMI and returns a structured
/// report. Leader-gated through the same <c>voice:ami:owner:leader</c> lease as
/// <see cref="VoiceCallControlService"/> — only the AMI-owner pod holds the live AMI connection, so a
/// follower pod cannot run the queries and returns a clean not-leader result.
/// </summary>
/// <remarks>
/// PJSIP id scheme (mirrors the Realtime sync engine): a trunk's endpoint is <c>t-{id}</c>, its
/// outbound registration is <c>reg-t-{id}</c>, its IP-ACL identify is <c>ipauth-t-{id}</c>. All output
/// parsing is intentionally tolerant (case-insensitive <c>Contains</c>, never throws on unexpected
/// text) — Asterisk CLI output is not a stable contract.
/// </remarks>
internal sealed partial class TrunkConnectivityTester : ITrunkConnectivityTester
{
    private const string AuthModeIpAcl = "ip-acl";
    private const string AuthModeRegister = "register";
    private const string AuthModeNone = "none";

    private readonly TrunkStoreBase _trunks;
    private readonly VerbaraServerPool _serverPool;
    private readonly IClusterLeader _leader;
    private readonly ILogger<TrunkConnectivityTester> _logger;

    public TrunkConnectivityTester(
        TrunkStoreBase trunks,
        VerbaraServerPool serverPool,
        [FromKeyedServices(VoiceLeaderResources.AmiOwner)] IClusterLeader leader,
        ILogger<TrunkConnectivityTester> logger)
    {
        _trunks = trunks;
        _serverPool = serverPool;
        _leader = leader;
        _logger = logger;
    }

    public async Task<TrunkConnectivityResult> TestAsync(TenantId tenantId, long trunkId, CancellationToken ct)
    {
        var endpointId = $"t-{trunkId}";

        // Only the AMI-owner leader holds the live AMI connection (mirrors VoiceCallControlService).
        if (!_leader.IsLeader)
            return NotLeaderResult(trunkId, endpointId);

        // Tenant-scoped resolution doubles as the ownership check: a trunk from another tenant
        // resolves to null here.
        var trunk = await _trunks.GetAsync(trunkId, tenantId.Value, ct).ConfigureAwait(false);
        if (trunk is null)
        {
            LogTrunkNotFound(trunkId, tenantId.Value);
            return new TrunkConnectivityResult(
                trunkId, endpointId, EndpointFound: false, AuthModeNone,
                Registered: null, IdentifyPresent: null, Reachable: null, Ok: false,
                Messages: [ITrunkConnectivityTester.TrunkNotFoundMessage]);
        }

        var authMode = ResolveAuthMode(trunk);

        var server = _serverPool.GetServer("primary");
        if (server is null)
            return AmiUnavailableResult(trunkId, endpointId, authMode);

        var messages = new List<string>();
        bool endpointFound;
        bool? reachable = null;
        bool? registered = null;
        bool? identifyPresent = null;

        try
        {
            // 1) Endpoint presence + reachability hint.
            var endpointOutput = await RunCommandAsync(server, $"pjsip show endpoint {endpointId}", ct)
                .ConfigureAwait(false);
            endpointFound = EndpointLinePresent(endpointOutput, endpointId);
            if (endpointFound)
            {
                reachable = EndpointReachable(endpointOutput);
                messages.Add($"Endpoint {endpointId} presente");
                messages.Add(reachable == true
                    ? "Endpoint alcanzable (contacto disponible)"
                    : "Endpoint sin contacto disponible — puede estar caído o sin registro aún");
            }
            else
            {
                messages.Add($"Endpoint {endpointId} NO presente — ¿el sync con Asterisk corrió? (Realtime)");
            }

            // 2) Auth-mode-specific check.
            switch (authMode)
            {
                case AuthModeRegister:
                {
                    var regOutput = await RunCommandAsync(server, "pjsip show registrations", ct)
                        .ConfigureAwait(false);
                    registered = RegistrationRegistered(regOutput, trunkId);
                    messages.Add(registered == true
                        ? "Registrado contra el carrier"
                        : "Sin registro — revisá credenciales/URI");
                    break;
                }
                case AuthModeIpAcl:
                {
                    var idOutput = await RunCommandAsync(server, $"pjsip show identify ipauth-{endpointId}", ct)
                        .ConfigureAwait(false);
                    identifyPresent = IdentifyPresent(idOutput);
                    if (identifyPresent == true)
                        messages.Add($"IP-ACL identify presente: {ExtractMatch(idOutput)}");
                    else
                        messages.Add("IP-ACL identify ausente — revisá matchHost / recargá res_pjsip_endpoint_identifier_ip");
                    break;
                }
                default:
                    messages.Add("Sin modo de autenticación (ni registro ni IP-ACL) — sólo presencia del endpoint");
                    break;
            }
        }
        catch (Exception ex)
        {
            LogAmiQueryFailed(trunkId, ex.Message);
            return new TrunkConnectivityResult(
                trunkId, endpointId, EndpointFound: false, authMode,
                Registered: null, IdentifyPresent: null, Reachable: null, Ok: false,
                Messages: ["no se pudo consultar Asterisk (AMI)"]);
        }

        var ok = endpointFound && authMode switch
        {
            AuthModeRegister => registered == true,
            AuthModeIpAcl => identifyPresent == true,
            _ => true,
        };

        LogTested(trunkId, authMode, ok);
        return new TrunkConnectivityResult(
            trunkId, endpointId, endpointFound, authMode, registered, identifyPresent, reachable, ok, messages);
    }

    /// <summary>Result for a follower pod (no live AMI connection cluster-wide except on the leader).</summary>
    private static TrunkConnectivityResult NotLeaderResult(long trunkId, string endpointId) =>
        new(trunkId, endpointId, EndpointFound: false, AuthModeNone,
            Registered: null, IdentifyPresent: null, Reachable: null, Ok: false,
            Messages: ["este pod no es el owner AMI"]);

    /// <summary>Result when the leader pod is up but has no connected Asterisk server in the pool.</summary>
    private static TrunkConnectivityResult AmiUnavailableResult(long trunkId, string endpointId, string authMode) =>
        new(trunkId, endpointId, EndpointFound: false, authMode,
            Registered: null, IdentifyPresent: null, Reachable: null, Ok: false,
            Messages: ["no se pudo consultar Asterisk (AMI)"]);

    private static async Task<string> RunCommandAsync(VerbaraServer server, string command, CancellationToken ct)
    {
        var response = await server.Connection
            .SendActionAsync<CommandResponse>(new CommandAction { Command = command }, ct)
            .ConfigureAwait(false);
        return response.Output ?? "";
    }

    /// <summary>
    /// IP-ACL when <c>MatchHost</c> is set; otherwise register when there is a registration URI or
    /// outbound auth username; otherwise none (presence-only). Mirrors the Realtime sync engine's
    /// branch order.
    /// </summary>
    private static string ResolveAuthMode(Verbara.Sdk.Pro.Dialer.Models.Trunk trunk)
    {
        if (!string.IsNullOrWhiteSpace(trunk.MatchHost))
            return AuthModeIpAcl;
        if (!string.IsNullOrWhiteSpace(trunk.RegistrationUri) || !string.IsNullOrWhiteSpace(trunk.AuthUsername))
            return AuthModeRegister;
        return AuthModeNone;
    }

    /// <summary>
    /// True when an <c>Endpoint:</c> line mentions the endpoint id, e.g.
    /// <c> Endpoint:  t-1   Unavailable   0 of inf</c>.
    /// </summary>
    private static bool EndpointLinePresent(string output, string endpointId)
    {
        foreach (var line in EnumerateLines(output))
        {
            if (line.Contains("Endpoint:", StringComparison.OrdinalIgnoreCase)
                && ContainsToken(line, endpointId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reachability hint off the <c>Endpoint:</c> state token: <c>Not in use</c> / <c>In use</c> /
    /// <c>Avail</c> ⇒ reachable; <c>Unavailable</c> (or anything else) ⇒ not.
    /// </summary>
    private static bool EndpointReachable(string output)
    {
        foreach (var line in EnumerateLines(output))
        {
            if (!line.Contains("Endpoint:", StringComparison.OrdinalIgnoreCase))
                continue;

            // "Unavailable" contains "Avail" as a substring, so check it FIRST.
            if (line.Contains("Unavailable", StringComparison.OrdinalIgnoreCase))
                return false;
            if (line.Contains("Not in use", StringComparison.OrdinalIgnoreCase)
                || line.Contains("In use", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Avail", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// True when a line mentioning <c>reg-t-{id}</c> also says <c>Registered</c>, e.g.
    /// <c> reg-t-2/sip:chicago.voip.ms   ...   Registered</c>.
    /// </summary>
    private static bool RegistrationRegistered(string output, long trunkId)
    {
        var regId = $"reg-t-{trunkId}";
        foreach (var line in EnumerateLines(output))
        {
            if (ContainsToken(line, regId)
                && line.Contains("Registered", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when <c>pjsip show identify</c> output carries a <c>Match:</c> row.</summary>
    private static bool IdentifyPresent(string output) =>
        output.Contains("Match:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the value after the first <c>Match:</c> token (best-effort, for the message).</summary>
    private static string ExtractMatch(string output)
    {
        foreach (var line in EnumerateLines(output))
        {
            var idx = line.IndexOf("Match:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return line[(idx + "Match:".Length)..].Trim();
        }
        return "";
    }

    /// <summary>Case-insensitive substring test (the endpoint/registration id may sit anywhere on the line).</summary>
    private static bool ContainsToken(string line, string token) =>
        line.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string[] EnumerateLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[TRUNK-TEST] Trunk {TrunkId} ({AuthMode}) connectivity test: ok={Ok}")]
    private partial void LogTested(long trunkId, string authMode, bool ok);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[TRUNK-TEST] Trunk {TrunkId} not found for tenant {TenantId}")]
    private partial void LogTrunkNotFound(long trunkId, string tenantId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[TRUNK-TEST] AMI query for trunk {TrunkId} failed: {Reason}")]
    private partial void LogAmiQueryFailed(long trunkId, string reason);
}
