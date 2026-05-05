using System.Net;

namespace Verbara.Platform.Identity;

/// <summary>
/// Pure evaluator: given a client IP and a tenant's allowlist entries,
/// decide whether the IP is allowed. Stateless; no caching, no storage.
/// Empty entries returns false (fail-closed — see spec §4.3).
/// </summary>
public interface IIpAllowlistEvaluator
{
    bool IsAllowed(IPAddress clientIp, IReadOnlyList<IpAllowlistEntry> entries);
}
