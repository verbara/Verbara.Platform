using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Core;

/// <summary>
/// Raised when a user's account may no longer authenticate — its status left Active, or it was
/// deleted. Every node holding live connections for the user (Realtime hub connections, Api SSE
/// streams) aborts them on receipt. It is the one push event that ends connections instead of
/// feeding them.
/// </summary>
/// <remarks>
/// <para>
/// User-targeted: <see cref="Metadata"/> carries <see cref="UserId"/>, so no delivery path can fan
/// it out as a tenant-wide broadcast.
/// </para>
/// <para>
/// Cross-pod: registered in <see cref="PlatformPushJsonContext"/> (the backplane payload), decoded
/// by Realtime's <c>RemoteEventDispatcher</c>, and read by <see cref="UserAccessRevocationListener"/>
/// on every node through <see cref="TryRead"/>, which also accepts the raw backplane envelope —
/// Api replicas have no dispatcher, and must not re-publish a typed copy (their relay would forward
/// it back onto the backplane).
/// </para>
/// </remarks>
/// <param name="TenantId">The tenant the account belongs to (for an impersonating admin, their home tenant).</param>
/// <param name="UserId">The account whose live connections end.</param>
/// <param name="Reason">Why access ended: the new status in lower case (<c>suspended</c>, <c>deactivated</c>) or <c>deleted</c>.</param>
public sealed record UserAccessRevokedEvent(string TenantId, string UserId, string Reason)
    : PlatformEvent(TenantId, EventTypeName, DateTimeOffset.UtcNow), ICrossPodEvent
{
    /// <summary>The wire discriminator (<see cref="PlatformEvent.Type"/>) of this event.</summary>
    public const string EventTypeName = "user.access_revoked";

    /// <summary>The <see cref="Reason"/> of a revocation caused by deleting the account (admin delete, GDPR erasure).</summary>
    public const string DeletedReason = "deleted";

    /// <inheritdoc />
    public override PushEventMetadata Metadata => new(TenantId, UserId, Timestamp, CorrelationId: null);

    /// <summary>
    /// Reads the event from either form a node receives it in: the typed record (published on this
    /// node, or re-published by Realtime's dispatcher) or the <see cref="RemotePushEvent"/> envelope
    /// the Redis backplane delivers from another node.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> for any other event, and for an envelope whose payload is empty,
    /// malformed or incomplete — a node without a payload serializer (Realtime) echoes re-published
    /// events back onto the backplane with an empty payload.
    /// </returns>
    public static bool TryRead(PushEvent pushEvent, [NotNullWhen(true)] out UserAccessRevokedEvent? revoked)
    {
        ArgumentNullException.ThrowIfNull(pushEvent);

        revoked = pushEvent switch
        {
            UserAccessRevokedEvent typed => typed,
            RemotePushEvent { OriginalEventType: EventTypeName, RawPayload.Length: > 0 } envelope => Decode(envelope.RawPayload),
            _ => null,
        };

        if (revoked is { TenantId.Length: > 0, UserId.Length: > 0 })
            return true;

        revoked = null;
        return false;
    }

    private static UserAccessRevokedEvent? Decode(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize(payload, PlatformPushJsonContext.Default.UserAccessRevokedEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
