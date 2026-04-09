namespace Asterisk.Platform.Queues.Services;

public sealed class AgentCapacityRecord
{
    public required string TenantId { get; init; }
    public required string AgentId { get; init; }
    public int VoiceLoad { get; set; }
    public int ChatLoad { get; set; }
    public int EmailLoad { get; set; }
    public int SmsLoad { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
