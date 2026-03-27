namespace Asterisk.Platform.Identity;

public interface IPermissionStore
{
    Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct);
}
