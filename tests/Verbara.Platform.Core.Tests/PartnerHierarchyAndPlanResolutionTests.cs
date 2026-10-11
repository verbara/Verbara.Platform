using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Core.Tests;

/// <summary>
/// partner-channel-pilot-fixes 1.7b: the two shared helpers — the plan a tenant is born with (design D15) and the
/// "is a Partner's Customer" predicate (design D16).
/// </summary>
public sealed class PartnerHierarchyAndPlanResolutionTests
{
    private static Tenant Make(string id, TenantType type, string? parent, TenantPlan? plan = null) => new()
    {
        TenantId = id,
        Name = id,
        Status = TenantStatus.Active,
        Type = type,
        ParentTenantId = parent,
        Metadata = plan is { } p ? new Dictionary<string, string> { ["Plan"] = p.ToString() } : null,
    };

    // ─── TenantPlanResolution.Resolve ──────────────────────────────────────────

    [Fact]
    public void Resolve_ShouldInheritParentPlan_WhenNoPlanRequested()
    {
        var resolution = TenantPlanResolution.Resolve(Make("p1", TenantType.Partner, "platform", TenantPlan.Pro), null);

        resolution.Kind.Should().Be(TenantPlanResolutionKind.Inherited);
        resolution.Plan.Should().Be(TenantPlan.Pro);
        resolution.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Resolve_ShouldInheritTheStarterFallback_WhenParentHasNoPlan()
    {
        TenantPlanResolution.Resolve(Make("platform", TenantType.Platform, null), null).Plan.Should().Be(TenantPlan.Starter);
    }

    [Theory]
    [InlineData("Starter", TenantPlan.Starter)]
    [InlineData("pro", TenantPlan.Pro)]
    [InlineData(" Enterprise ", TenantPlan.Enterprise)]
    public void Resolve_ShouldKeepThePlan_WhenAtOrBelowParent(string requested, TenantPlan expected)
    {
        var resolution = TenantPlanResolution.Resolve(Make("platform", TenantType.Platform, null, TenantPlan.Enterprise), requested);

        resolution.Kind.Should().Be(TenantPlanResolutionKind.Kept);
        resolution.Plan.Should().Be(expected);
        resolution.IsAccepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("Gold")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("7")]
    public void Resolve_ShouldReportUnknownName_WhenValueIsNotAPlanName(string requested)
    {
        var resolution = TenantPlanResolution.Resolve(Make("platform", TenantType.Platform, null, TenantPlan.Enterprise), requested);

        resolution.Kind.Should().Be(TenantPlanResolutionKind.UnknownName);
        resolution.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Resolve_ShouldReportAboveParent_WhenPlanExceedsParentPlan()
    {
        var resolution = TenantPlanResolution.Resolve(Make("p1", TenantType.Partner, "platform", TenantPlan.Pro), "Enterprise");

        resolution.Kind.Should().Be(TenantPlanResolutionKind.AboveParent);
        resolution.Plan.Should().Be(TenantPlan.Enterprise);
        resolution.ParentPlan.Should().Be(TenantPlan.Pro);
        resolution.IsAccepted.Should().BeFalse();
    }

    // ─── PartnerHierarchy.IsPartnerCustomerAsync ────────────────────────────────

    private static ITenantStore Store(params Tenant[] tenants)
    {
        var store = Substitute.For<ITenantStore>();
#pragma warning disable CA2012 // ValueTask used in NSubstitute mock setup
        store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ReturnsNull();
        foreach (var tenant in tenants)
            store.GetAsync(tenant.TenantId, Arg.Any<CancellationToken>()).Returns(tenant);
#pragma warning restore CA2012
        return store;
    }

    private static readonly Tenant Platform = Make("platform", TenantType.Platform, null);
    private static readonly Tenant Partner = Make("p1", TenantType.Partner, "platform");
    private static readonly Tenant PartnerCustomer = Make("pc1", TenantType.Customer, "p1");
    private static readonly Tenant DirectCustomer = Make("dc1", TenantType.Customer, "platform");
    private static readonly Tenant OrphanCustomer = Make("oc1", TenantType.Customer, "gone");

    [Theory]
    [InlineData("pc1", true)]
    [InlineData("dc1", false)]
    [InlineData("p1", false)]
    [InlineData("platform", false)]
    [InlineData("missing", false)]
    [InlineData("oc1", false)]
    public async Task IsPartnerCustomerAsync_ShouldHoldOnlyForAPartnersCustomer(string tenantId, bool expected)
    {
        var store = Store(Platform, Partner, PartnerCustomer, DirectCustomer, OrphanCustomer);

        (await PartnerHierarchy.IsPartnerCustomerAsync(store, tenantId)).Should().Be(expected);
    }

    [Fact]
    public async Task GetBillingPartnerAsync_ShouldReturnTheParentPartner_WhenTenantIsAPartnersCustomer()
    {
        var store = Store(Platform, Partner, PartnerCustomer);

        (await PartnerHierarchy.GetBillingPartnerAsync(store, "pc1"))!.TenantId.Should().Be("p1");
    }
}
