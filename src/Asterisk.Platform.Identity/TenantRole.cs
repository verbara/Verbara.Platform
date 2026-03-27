using Asterisk.Platform.Core;

namespace Asterisk.Platform.Identity;

public sealed class TenantRole
{
    public required string RoleId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? SourceTemplateId { get; init; }
    public bool IsDefault { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public IReadOnlyList<string>? Permissions { get; set; }
}
