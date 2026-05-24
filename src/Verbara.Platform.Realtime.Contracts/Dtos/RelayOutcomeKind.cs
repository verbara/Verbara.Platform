using System.Text.Json.Serialization;

namespace Verbara.Platform.Realtime.Contracts.Dtos;

/// <summary>
/// Outcome classification recorded by <c>PushToHubRelay</c> for every
/// push-event the realtime pod observes. Surfaced via
/// <c>GET /admin/realtime/audit</c> so E2E harnesses + on-call operators can
/// assert exactly-once delivery semantics across a multi-pod cluster.
/// </summary>
/// <remarks>
/// Serialised as JSON strings so the audit payload stays human-readable when
/// inspected via <c>curl</c> or <c>jq</c>. The <see cref="JsonStringEnumConverter{TEnum}"/>
/// generic converter is AOT-clean (replaces the reflection-based non-generic
/// converter that would trip the trim analyzer in Verbara.Platform.Api).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<RelayOutcomeKind>))]
public enum RelayOutcomeKind
{
    /// <summary>The leader pod successfully published the event to a SignalR group.</summary>
    Forwarded = 0,

    /// <summary>This pod is not the leader for the fanout resource — short-circuited per ADR-0022 Phase A.5.</summary>
    SkippedNotLeader = 1,

    /// <summary>The event metadata carried a null/empty TenantId — cannot route to a tenant group.</summary>
    SkippedNullTenant = 2,

    /// <summary>A cluster-node event arrived with a null/empty NodeId — cannot publish a coherent payload.</summary>
    SkippedNullNodeId = 3,

    /// <summary>The hub broadcast threw — Redis backplane down, group serialisation error, etc.</summary>
    ForwardError = 4,
}
