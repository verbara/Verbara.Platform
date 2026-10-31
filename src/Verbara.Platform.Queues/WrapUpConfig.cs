namespace Verbara.Platform.Queues;

public sealed class WrapUpConfig
{
    /// <summary>The wrap-up of a Platform queue that sets none, and of a queue Platform does not know.</summary>
    public const int FallbackSeconds = 30;

    public int DefaultWrapUpSeconds { get; set; } = FallbackSeconds;
    public bool ForceWrapUp { get; set; }
}
