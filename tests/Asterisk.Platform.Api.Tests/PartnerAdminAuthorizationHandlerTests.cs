using System.Security.Claims;
using Asterisk.Platform.Api.Auth;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

/// <summary>Unit tests for PartnerAdminAuthorizationHandler — no HTTP involved.</summary>
public sealed class PartnerAdminAuthorizationHandlerTests
{
    private static ClaimsPrincipal MakePrincipal(string tenantId) =>
        new(new ClaimsIdentity(
        [
            new Claim("tenant_id", tenantId),
            new Claim("sub", "user-001"),
        ], "Test"));

    private static PartnerAdminAuthorizationHandler BuildHandler(ITenantStore tenantStore)
    {
        var resolver = new PermissionResolver(Substitute.For<Asterisk.Platform.Identity.IUserRoleStore>());
        return new PartnerAdminAuthorizationHandler(tenantStore, resolver);
    }

    [Fact]
    public async Task HandleRequirement_ShouldSucceed_WhenTenantIsActivePartner()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAsync("partner-001").Returns(new Tenant
        {
            TenantId = "partner-001",
            Name = "Partner",
            Type = TenantType.Partner,
            Status = TenantStatus.Active,
        });

        var handler = BuildHandler(tenantStore);
        var principal = MakePrincipal("partner-001");
        var requirement = new PartnerAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_ShouldFail_WhenTenantIsCustomer()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAsync("customer-001").Returns(new Tenant
        {
            TenantId = "customer-001",
            Name = "Customer",
            Type = TenantType.Customer,
            Status = TenantStatus.Active,
        });

        var handler = BuildHandler(tenantStore);
        var principal = MakePrincipal("customer-001");
        var requirement = new PartnerAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirement_ShouldFail_WhenTenantIsPlatform()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAsync("platform").Returns(new Tenant
        {
            TenantId = "platform",
            Name = "Platform",
            Type = TenantType.Platform,
            Status = TenantStatus.Active,
        });

        var handler = BuildHandler(tenantStore);
        var principal = MakePrincipal("platform");
        var requirement = new PartnerAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirement_ShouldFail_WhenPartnerIsSuspended()
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAsync("partner-suspended").Returns(new Tenant
        {
            TenantId = "partner-suspended",
            Name = "Suspended Partner",
            Type = TenantType.Partner,
            Status = TenantStatus.Suspended,
        });

        var handler = BuildHandler(tenantStore);
        var principal = MakePrincipal("partner-suspended");
        var requirement = new PartnerAdminRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }
}
