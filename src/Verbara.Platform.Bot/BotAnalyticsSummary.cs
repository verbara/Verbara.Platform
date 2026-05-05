namespace Verbara.Platform.Bot;

/// <summary>
/// Aggregated bot analytics for a given tenant and time period.
/// </summary>
public sealed class BotAnalyticsSummary
{
    public int TotalConversations { get; init; }
    public int HandedOff { get; init; }
    public int Resolved { get; init; }
    public int Failed { get; init; }
    public double HandoffRate { get; init; }
    public double ResolutionRate { get; init; }
    public double AvgTurns { get; init; }
    public double FailureRate { get; init; }
}
