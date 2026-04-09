using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations;

public sealed class CannedResponse
{
    public required EntityId ResponseId { get; init; }
    public required TenantId TenantId { get; init; }
    public required string Shortcut { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? Category { get; set; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
