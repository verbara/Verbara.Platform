using Asterisk.Platform.Core;

namespace Asterisk.Platform.Queues;

public sealed class Team : ITenantScoped, IAuditable
{
    public required EntityId TeamId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public EntityId? SupervisorId { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
}
