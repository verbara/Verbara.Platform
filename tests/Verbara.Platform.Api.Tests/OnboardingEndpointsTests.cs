using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

public sealed class OnboardingEndpointsTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static Tenant CreateTenant(Dictionary<string, string>? metadata = null) => new()
    {
        TenantId = "test-tenant-001",
        Name = "Test Company",
        Status = TenantStatus.Active,
        Type = TenantType.Customer,
        Metadata = metadata,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static bool ReadWizardCompleted(Tenant tenant)
    {
        var meta = tenant.Metadata ?? new Dictionary<string, string>();
        return meta.GetValueOrDefault("OnboardingCompleted") == "true";
    }

    private static string? ReadTemplateApplied(Tenant tenant)
    {
        var meta = tenant.Metadata ?? new Dictionary<string, string>();
        return meta.TryGetValue("OnboardingTemplate", out var tpl) ? tpl : null;
    }

    private static bool ReadChecklistDismissed(Tenant tenant)
    {
        var meta = tenant.Metadata ?? new Dictionary<string, string>();
        return meta.GetValueOrDefault("OnboardingDismissedChecklist") == "true";
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OnboardingStatus_ShouldShowNotCompleted_WhenMetadataAbsent()
    {
        var tenant = CreateTenant(metadata: null);

        var completed = ReadWizardCompleted(tenant);
        var template = ReadTemplateApplied(tenant);
        var dismissed = ReadChecklistDismissed(tenant);

        completed.Should().BeFalse();
        template.Should().BeNull();
        dismissed.Should().BeFalse();
    }

    [Fact]
    public void OnboardingStatus_ShouldShowCompleted_WhenMetadataPresent()
    {
        var tenant = CreateTenant(new Dictionary<string, string>
        {
            ["OnboardingCompleted"] = "true",
        });

        var completed = ReadWizardCompleted(tenant);

        completed.Should().BeTrue();
    }

    [Fact]
    public void OnboardingStatus_ShouldShowTemplateApplied()
    {
        var tenant = CreateTenant(new Dictionary<string, string>
        {
            ["OnboardingTemplate"] = "support",
        });

        var template = ReadTemplateApplied(tenant);

        template.Should().Be("support");
    }

    [Fact]
    public void OnboardingStatus_ShouldShowChecklistDismissed()
    {
        var tenant = CreateTenant(new Dictionary<string, string>
        {
            ["OnboardingDismissedChecklist"] = "true",
        });

        var dismissed = ReadChecklistDismissed(tenant);

        dismissed.Should().BeTrue();
    }
}
