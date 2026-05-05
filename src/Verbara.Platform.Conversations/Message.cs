using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed class Message : ITenantScoped, IAuditable
{
    public required EntityId MessageId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required TenantId TenantId { get; init; }
    public required MessageDirection Direction { get; init; }
    public required ChannelType Channel { get; init; }
    public string? SenderId { get; init; }
    public required MessageEnvelope Content { get; init; }
    public required MessageDeliveryStatus DeliveryStatus { get; set; }
    public string? ExternalMessageId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
}
