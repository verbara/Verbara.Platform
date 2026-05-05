namespace Verbara.Platform.Billing;

public sealed class DunningConfig
{
    public int WarningDays { get; set; }
    public int DegradedDays { get; set; } = 7;
    public int SuspendedDays { get; set; } = 14;
    public int PendingDeletionDays { get; set; } = 30;
    public int CheckIntervalHours { get; set; } = 6;
}
