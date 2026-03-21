using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core;

public sealed class TenantChannelConfig : ITenantScoped
{
    public required TenantId TenantId { get; init; }
    public required ChannelType Channel { get; init; }
    public required IReadOnlyDictionary<string, string> Credentials { get; init; }
    public bool IsActive { get; set; } = true;
}
