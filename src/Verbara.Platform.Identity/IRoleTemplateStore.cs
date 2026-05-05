namespace Verbara.Platform.Identity;

public interface IRoleTemplateStore
{
    Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct);
    Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct);
}
