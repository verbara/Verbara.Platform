using Asterisk.Platform.Core;

namespace Asterisk.Platform.Flows;

/// <summary>
/// Persistence contract for flow definitions.
/// </summary>
public interface IFlowStore
{
    /// <summary>Gets any version of a flow by its ID.</summary>
    Task<FlowDefinition?> GetByIdAsync(TenantId tenantId, EntityId flowId, CancellationToken ct);

    /// <summary>Gets the published (active) version of a flow.</summary>
    Task<FlowDefinition?> GetPublishedAsync(TenantId tenantId, EntityId flowId, CancellationToken ct);

    /// <summary>Persists a flow definition.</summary>
    Task SaveAsync(FlowDefinition flow, CancellationToken ct);
}
