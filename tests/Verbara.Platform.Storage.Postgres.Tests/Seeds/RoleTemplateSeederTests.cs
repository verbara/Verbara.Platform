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

    // ── billing:credits:* (c1 credit-ledger-topups) ────────────────────────

    [Fact]
    public void CanonicalPermissions_ShouldContainBillingCreditsRead()
    {
        // billing:credits:read is in AllPermissions() so tenant admins (admin/system_admin) and
        // platform_admin can all read their own AI-credit balance + ledger.
        RoleTemplateSeeder.GetCanonicalPermissions()
            .Should().Contain("billing:credits:read");
    }

    [Fact]
    public void CanonicalPermissions_ShouldNotContainBillingCreditsGrant()
    {
        // billing:credits:grant is deliberately NOT in AllPermissions() — otherwise the admin /
        // system_admin templates (AllPermissionsExcept) would inherit the cross-tenant top-up mint.
        RoleTemplateSeeder.GetCanonicalPermissions()
            .Should().NotContain("billing:credits:grant");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    public void Seeder_ShouldGrantBillingCreditsRead_ToAdminTemplates(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().Contain("billing:credits:read",
            $"template '{templateId}' must be granted billing:credits:read");
    }

    [Fact]
    public void Seeder_ShouldGrantBillingCreditsGrant_ToPlatformAdminOnly()
    {
        RoleTemplateSeeder.GetTemplatePermissions("platform_admin")
            .Should().Contain("billing:credits:grant");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    public void Seeder_ShouldNotGrantBillingCreditsGrant_ToTenantAdminTemplates(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().NotContain("billing:credits:grant",
            $"template '{templateId}' must NOT be granted the operator billing:credits:grant");
    }
}
