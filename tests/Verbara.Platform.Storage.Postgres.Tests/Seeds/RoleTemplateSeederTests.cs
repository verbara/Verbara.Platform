using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// Pure-unit coverage of the canonical role-template → permission grants exposed by
/// <see cref="RoleTemplateSeeder"/> (no Postgres required).
/// </summary>
public sealed class RoleTemplateSeederTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    public void RoleTemplate_ShouldGrantTypificationConfigure_WhenAdminTemplate(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().Contain("system:typification:configure");
    }

    [Fact]
    public void RoleTemplate_ShouldGrantTypificationConfigureWhereverTenantConfigureIsGranted_WhenAnyTemplate()
    {
        foreach (var templateId in new[]
                 {
                     "agent", "supervisor", "quality_analyst", "manager", "admin",
                     "system_admin", "api", "platform_admin",
                     "partner_admin", "partner_billing", "partner_viewer",
                 })
        {
            var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);
            if (permissions.Contains("system:tenant:configure"))
                permissions.Should().Contain("system:typification:configure",
                    $"template '{templateId}' grants system:tenant:configure so it must also grant the typification sibling");
        }
    }

    [Fact]
    public void CanonicalPermissions_ShouldContainTypificationConfigure()
    {
        RoleTemplateSeeder.GetCanonicalPermissions()
            .Should().Contain("system:typification:configure");
    }
}
