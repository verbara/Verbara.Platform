using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed class Tag : ITenantScoped
{
    public required EntityId TagId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required TagSource Source { get; init; }
    public string? Color { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
