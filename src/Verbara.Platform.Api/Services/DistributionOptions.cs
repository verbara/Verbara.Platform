namespace Verbara.Platform.Api.Services;

public sealed class DistributionOptions
{
    public int PollIntervalMs { get; set; } = 2000;
    public int OfferTimeoutSeconds { get; set; } = 30;
    public int DefaultQueueTimeoutSeconds { get; set; } = 300;
    public int DefaultWrapUpTimeoutSeconds { get; set; } = 120;
    public int MaxConversationsPerCycle { get; set; } = 50;

    /// <summary>
    /// Startup grace period (milliseconds) before <c>ConversationTimeoutWorker</c> begins its
    /// first sweep, letting dependencies warm up. Options-overridable for deterministic fast
    /// tests (C3); default preserves production cadence.
    /// </summary>
    public int ConversationTimeoutStartupDelayMs { get; set; } = 5000;

    /// <summary>
    /// Interval (milliseconds) between <c>ConversationTimeoutWorker</c> timeout sweeps; also the
    /// heartbeat tick cadence reported for the worker. Options-overridable for deterministic fast
    /// tests (C3); default preserves production cadence.
    /// </summary>
    public int ConversationTimeoutSweepIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Startup warm-up delay (milliseconds) before <c>QueueDistributionWorker</c> begins its
    /// first distribution pass. Options-overridable for deterministic fast tests (C3); default
    /// preserves production cadence.
    /// </summary>
    public int QueueDistributionStartupDelayMs { get; set; } = 3000;
}
