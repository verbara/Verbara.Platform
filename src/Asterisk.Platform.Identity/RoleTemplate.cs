namespace Asterisk.Platform.Identity;

public sealed class RoleTemplate
{
    public required string TemplateId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsSystem { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<string>? Permissions { get; set; }
}
