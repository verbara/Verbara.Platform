namespace Verbara.Platform.Queues;

public sealed class WrapUpConfig
{
    public int DefaultWrapUpSeconds { get; set; } = 30;
    public bool ForceWrapUp { get; set; }
}
