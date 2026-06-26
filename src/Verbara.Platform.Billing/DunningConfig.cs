namespace Verbara.Platform.Billing;

public sealed class DunningConfig
{
    private int _overageGraceDays = 3;
    private int _paymentTermDays = 14;

    public int WarningDays { get; set; }
    public int DegradedDays { get; set; } = 7;
    public int SuspendedDays { get; set; } = 14;
    public int PendingDeletionDays { get; set; } = 30;
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>
    /// Days after an invoice <c>PeriodEnd</c> before an overage draft is issued.
    /// Negative values are treated as <c>0</c> at read. Default <c>3</c>.
    /// </summary>
    public int OverageGraceDays
    {
        get => _overageGraceDays < 0 ? 0 : _overageGraceDays;
        set => _overageGraceDays = value;
    }

    /// <summary>
    /// Days after issuance before an overage invoice's <c>DueDate</c>.
    /// Negative values are treated as <c>0</c> at read. Default <c>14</c>.
    /// </summary>
    public int PaymentTermDays
    {
        get => _paymentTermDays < 0 ? 0 : _paymentTermDays;
        set => _paymentTermDays = value;
    }
}
