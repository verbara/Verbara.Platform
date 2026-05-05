using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public sealed class ServiceAccount : ITenantScoped, IAuditable
{
    public required EntityId AccountId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required IReadOnlyList<string> Scopes { get; set; }
    public bool IsActive { get; set; } = true;
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? CreatedBy { get; init; }
    public string? UpdatedBy { get; set; }
}
