using Verbara.Platform.Core;

namespace Verbara.Platform.Routing.Inbound;

/// <summary>
/// Phase 2 voice-routing primitive: maps an inbound DID (Direct Inward Dialing
/// number) to a target queue for a tenant. Consumed by the Stasis voice
/// consumer to resolve which queue a PSTN/SIP inbound leg should land in.
///
/// A DID is unique per tenant (<c>UNIQUE (tenant_id, did)</c>); the
/// store layer surfaces a violation as a domain-meaningful signal so the API
/// can return HTTP 409.
/// </summary>
public sealed class DidRoute : ITenantScoped
{
    public required EntityId RouteId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Did { get; set; }
    public required EntityId QueueId { get; set; }
    public bool IsActive { get; set; } = true;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
