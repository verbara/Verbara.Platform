namespace Verbara.Platform.Typification;

/// <summary>
/// Scope at which a <see cref="ReasonHint"/> applies, mapping a routing/entry
/// context (a DID, a channel, or a queue) to a captured reason taxonomy path.
/// </summary>
public enum ReasonHintScope
{
    Did = 0,
    Channel = 1,
    Queue = 2,
}
