namespace Verbara.Platform.Realtime.Services;

/// <summary>
/// Construction options for <see cref="RelayOutcomeSink"/>. Bound from
/// <c>RealtimeAudit:*</c> configuration in <c>Program.cs</c>.
/// </summary>
internal sealed class RelayOutcomeSinkOptions
{
    /// <summary>
    /// Maximum number of entries the ring buffer holds before overwriting the
    /// oldest. Larger values smooth over bursty E2E harness polls (less
    /// chance of silent eviction between record and read) at the cost of
    /// resident memory (≈ 320 B per <c>RelayOutcomeEntry</c>).
    /// </summary>
    public int Capacity { get; init; } = 10_000;

    /// <summary>
    /// Identity stamped on every recorded outcome + every audit page response.
    /// In K8s, the downward API projects <c>metadata.name</c> as the
    /// <c>POD_NAME</c> environment variable; <c>Program.cs</c> aliases it to
    /// <c>Cluster:InstanceId</c>. Defaults to <c>"local-dev"</c> when both are
    /// unset (Aspire dev loop, xunit test fixtures).
    /// </summary>
    public string PodInstanceId { get; init; } = "local-dev";
}
