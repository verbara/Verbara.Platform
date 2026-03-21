namespace Asterisk.Platform.Channels.Sms;

public sealed class SmsOptions
{
    public required string DefaultFromNumber { get; set; }
    public int MaxSegments { get; set; } = 3;
}
