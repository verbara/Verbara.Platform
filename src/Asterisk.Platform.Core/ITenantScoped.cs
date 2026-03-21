namespace Asterisk.Platform.Core;

public interface ITenantScoped
{
    TenantId TenantId { get; }
}
