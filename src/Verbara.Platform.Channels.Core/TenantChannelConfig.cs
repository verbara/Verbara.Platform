using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

public sealed class TenantChannelConfig : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required ChannelType Channel { get; init; }
    public required IReadOnlyDictionary<string, string> Credentials { get; init; }
    public bool IsActive { get; set; } = true;
}
