namespace Verbara.Platform.Core;

public interface ITenantScoped
{
    TenantId TenantId { get; }
}
