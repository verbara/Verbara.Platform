namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// The host switch of last-agent routing, section <c>InboundRouting:LastAgent</c> (conversations-return-to-last-agent
/// design D1), bound by the Api host with an explicit <c>TryParse</c> (AOT-safe). A queue may override
/// <see cref="Enabled"/> through <c>queue_configs.last_agent_routing_enabled</c>, read for the route's final queue
/// (design D2): the effective value is <c>queue ?? host</c>.
/// </summary>
public sealed class LastAgentRoutingOptions
{
    /// <summary>Default of <see cref="WindowHours"/>.</summary>
    public const int DefaultWindowHours = 72;

    /// <summary>Smallest valid <see cref="WindowHours"/>.</summary>
    public const int MinWindowHours = 1;

    /// <summary>Largest valid <see cref="WindowHours"/> (30 days).</summary>
    public const int MaxWindowHours = 720;

    /// <summary><c>InboundRouting:LastAgent:Enabled</c>: off unless the host turns it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// <c>InboundRouting:LastAgent:WindowHours</c>: how far back, from a conversation's last customer-facing message,
    /// it still names the preferred agent. 1 to 720; a value outside fails host start, never a silent clamp.
    /// </summary>
    public int WindowHours { get; set; } = DefaultWindowHours;
}
