namespace Asterisk.Platform.Queues;

/// <summary>
/// Configuration options for the queues subsystem.
/// </summary>
public sealed class QueueOptions
{
    /// <summary>
    /// Default maximum number of waiting items per queue.
    /// </summary>
    public int DefaultMaxWaiting { get; set; } = 100;

    /// <summary>
    /// Default wrap-up time in seconds after queue interaction ends.
    /// </summary>
    public int DefaultWrapUpSeconds { get; set; } = 30;
}
