namespace Verbara.Platform.Identity;

public sealed class PermissionDefinition
{
    public required string PermissionId { get; init; }
    public required string Category { get; init; }
    public required string Resource { get; init; }
    public required string Action { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Implies { get; init; }
}
