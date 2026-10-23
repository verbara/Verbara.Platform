namespace Verbara.Platform.Api.Services;

/// <summary>
/// The ids under which the Api registers its Asterisk servers in the cluster's <c>InitialNodes</c>. The Pro dialer's
/// node option is set from the same constant (dialer-places-calls-end-to-end, design D1), so the engine's node and
/// the cluster's key come from one value.
/// </summary>
public static class AsteriskNodeIds
{
    /// <summary>The Api's only Asterisk node.</summary>
    public const string Primary = "primary";
}
