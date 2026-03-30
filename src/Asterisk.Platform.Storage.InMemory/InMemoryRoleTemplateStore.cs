using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryRoleTemplateStore : IRoleTemplateStore
{
    private readonly List<RoleTemplate> _templates = [];
    private readonly Dictionary<string, List<string>> _permissions = new();

    public Task<IReadOnlyList<RoleTemplate>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RoleTemplate>>(_templates);

    public Task<RoleTemplate?> GetByIdAsync(string templateId, CancellationToken ct)
        => Task.FromResult(_templates.FirstOrDefault(t => t.TemplateId == templateId));

    public Task<IReadOnlyList<string>> GetPermissionsAsync(string templateId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(
            _permissions.GetValueOrDefault(templateId, []));
}
