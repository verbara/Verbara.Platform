using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed class Disposition : ITenantScoped
{
    public required EntityId DispositionId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required DispositionCategory Category { get; set; }
    public bool IsActive { get; set; } = true;
    public required DateTimeOffset CreatedAt { get; init; }
}
