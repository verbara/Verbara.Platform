namespace Verbara.Platform.Conversations;

/// <summary>How far a WebChat contact's identity is known. Computed from its addresses, never stored.</summary>
public enum ContactIdentityLevel
{
    /// <summary>Holds a WebChat address and no identity key: nothing links its sessions.</summary>
    Anonymous,

    /// <summary>Holds a browser key: the same browser came back, which proves nothing about who is using it.</summary>
    Unverified,

    /// <summary>Holds the customer id a token signed by the tenant's site vouched for.</summary>
    Verified,
}
