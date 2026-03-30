using Asterisk.Platform.Identity;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryPermissionStore : IPermissionStore
{
    private readonly List<PermissionDefinition> _permissions = [];

    public Task<IReadOnlyList<PermissionDefinition>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(_permissions);

    public Task<IReadOnlyList<PermissionDefinition>> GetByCategoryAsync(string category, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermissionDefinition>>(
            _permissions.Where(p => p.Category == category).ToList());
}
