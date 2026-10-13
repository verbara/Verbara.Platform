using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Verbara.Sdk.Pro.Dialer.Models;

namespace Verbara.Platform.Api.Services.OutboundTrunk;

/// <summary>
/// outbound-calls-reach-the-trunk (design D3): the host Asterisk sends a trunk's INVITE to. The trunk sync writes no
/// AOR contact for a trunk, so a dial has to name the address: the registration URI's <c>host[:port]</c> when it has
/// one, otherwise a <c>MatchHost</c> that names exactly one address. Anything else is not dialable (null).
/// </summary>
internal static class DialHost
{
    /// <summary>The dial host of <paramref name="trunk"/>, or null when it names none.</summary>
    public static string? From(Trunk trunk)
    {
        ArgumentNullException.ThrowIfNull(trunk);
        return FromRegistrationUri(trunk.RegistrationUri) ?? FromMatchHost(trunk.MatchHost);
    }

    /// <summary>
    /// The <c>host[:port]</c> of a <c>sip:</c> or <c>sips:</c> URI, without userinfo, parameters or headers; null when the
    /// value is absent or does not parse.
    /// </summary>
    private static string? FromRegistrationUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var value = uri.Trim();
        string rest;
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
            rest = value[4..];
        else if (value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            rest = value[5..];
        else
            return null;

        var end = rest.IndexOfAny([';', '?']);
        if (end >= 0)
            rest = rest[..end];
        var at = rest.LastIndexOf('@');
        if (at >= 0)
            rest = rest[(at + 1)..];

        return IsHostPort(rest) ? rest : null;
    }

    private static bool IsHostPort(string value)
    {
        if (value.Length == 0)
            return false;

        string host;
        string? port = null;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0
                || !IPAddress.TryParse(value[1..close], out var v6)
                || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            var after = value[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                    return false;
                port = after[1..];
            }

            return port is null || IsPort(port);
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            host = value[..colon];
            port = value[(colon + 1)..];
        }
        else
        {
            host = value;
        }

        return host.Length > 0
               && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
               && (port is null || IsPort(port));
    }

    private static bool IsPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;

    /// <summary>A bare IP address, or a CIDR whose prefix is the full address length (<c>/32</c>, <c>/128</c>).</summary>
    private static string? FromMatchHost(string? matchHost)
    {
        if (string.IsNullOrWhiteSpace(matchHost))
            return null;

        var value = matchHost.Trim();
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var address = slash < 0 ? value : value[..slash];
        if (!IPAddress.TryParse(address, out var ip))
            return null;

        var full = ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (slash >= 0
            && !(int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) && prefix == full))
            return null;

        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
    }
}
