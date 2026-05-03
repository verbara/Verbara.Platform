using System.Net;

namespace Asterisk.Platform.Identity;

/// <summary>
/// Sequential CIDR-match evaluator. O(N) over entries; N is typically small
/// (≤ 50 entries per tenant in practice) so a trie is overkill.
/// Each Cidr string is parsed via <see cref="IPNetwork.Parse(string)"/>;
/// malformed values bubble FormatException up to the caller (the store
/// guarantees canonical form on insert, so a malformed value implies
/// corruption that must surface).
/// </summary>
public sealed class DefaultIpAllowlistEvaluator : IIpAllowlistEvaluator
{
    public bool IsAllowed(IPAddress clientIp, IReadOnlyList<IpAllowlistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
            return false;

        foreach (var entry in entries)
        {
            var network = System.Net.IPNetwork.Parse(entry.Cidr);
            if (network.BaseAddress.AddressFamily != clientIp.AddressFamily)
                continue;
            if (network.Contains(clientIp))
                return true;
        }

        return false;
    }
}
