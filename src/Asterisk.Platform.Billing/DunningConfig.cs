namespace Asterisk.Platform.Billing;

public sealed class DunningConfig
{
    public int WarningDays { get; init; }
    public int DegradedDays { get; init; } = 7;
    public int SuspendedDays { get; init; } = 14;
    public int PendingDeletionDays { get; init; } = 30;
    public int CheckIntervalHours { get; init; } = 6;
}
