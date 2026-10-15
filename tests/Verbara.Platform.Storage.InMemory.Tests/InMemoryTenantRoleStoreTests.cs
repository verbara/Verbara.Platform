using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// <see cref="InMemoryTenantRoleStore.SaveAsync"/> keeps the template a role was cloned from, the way
/// <c>PostgresTenantRoleStore</c>'s upsert never writes <c>source_template_id</c> on an update.
/// </summary>
public sealed class InMemoryTenantRoleStoreTests
{
    private static readonly TenantId s_tenant = new("tenant-1");

    private readonly InMemoryTenantRoleStore _store = new();

    [Fact]
    public async Task SaveAsync_ShouldKeepTheSourceTemplate_WhenTheRoleIsRenamed()
    {
        await _store.CloneFromTemplateAsync(s_tenant, "owners", "admin", "Admin", null, CancellationToken.None);
        var role = (await _store.GetByIdAsync(s_tenant, "owners", CancellationToken.None))!;
        role.Name = "Owners";

        await _store.SaveAsync(role, CancellationToken.None);

        var saved = (await _store.ListAsync(s_tenant, CancellationToken.None)).Should().ContainSingle().Subject;
        saved.Name.Should().Be("Owners");
        saved.SourceTemplateId.Should().Be("admin");
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepTheSourceTemplate_WhenTheSavedRoleCarriesAnother()
    {
        await _store.CloneFromTemplateAsync(s_tenant, "owners", "admin", "Admin", null, CancellationToken.None);

        await _store.SaveAsync(
            new TenantRole { RoleId = "owners", TenantId = s_tenant, Name = "Owners", CreatedAt = DateTimeOffset.UtcNow },
            CancellationToken.None);

        var saved = (await _store.ListAsync(s_tenant, CancellationToken.None)).Should().ContainSingle().Subject;
        saved.Name.Should().Be("Owners");
        saved.SourceTemplateId.Should().Be("admin");
    }
}
