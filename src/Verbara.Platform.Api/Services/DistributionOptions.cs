namespace Verbara.Platform.Api.Services;

public sealed class DistributionOptions
{
    public int PollIntervalMs { get; set; } = 2000;
    public int OfferTimeoutSeconds { get; set; } = 30;
    public int DefaultQueueTimeoutSeconds { get; set; } = 300;
    public int DefaultWrapUpTimeoutSeconds { get; set; } = 120;
    public int MaxConversationsPerCycle { get; set; } = 50;
}
