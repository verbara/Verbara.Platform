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

    // ── typification:ai:* (P2b A5) ─────────────────────────────────────────

    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    [InlineData("manager")]
    public void Seeder_ShouldGrantTypificationAiConfigure_ToAdminAndManagerTemplates(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().Contain("typification:ai:configure",
            $"template '{templateId}' must be granted typification:ai:configure");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    public void Seeder_ShouldGrantTypificationAiAutonomous_ToAdminTemplatesOnly(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().Contain("typification:ai:autonomous",
            $"template '{templateId}' must be granted typification:ai:autonomous");
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("agent")]
    public void Seeder_ShouldNotGrantTypificationAiAutonomous_ToManagerOrAgent(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().NotContain("typification:ai:autonomous",
            $"template '{templateId}' must NOT be granted typification:ai:autonomous");
    }
}
