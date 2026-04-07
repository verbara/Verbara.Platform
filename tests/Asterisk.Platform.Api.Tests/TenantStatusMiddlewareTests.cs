using Asterisk.Platform.Api.Middleware;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class TenantStatusMiddlewareTests
{
    private readonly ITenantStore _tenantStore = Substitute.For<ITenantStore>();
    private readonly ITenantAddOnStore _addOnStore = Substitute.For<ITenantAddOnStore>();
    private readonly TenantTierCache _tierCache = new();
    private readonly FeatureGateCache _featureGateCache = new();
    private bool _nextCalled;

    private TenantStatusMiddleware CreateMiddleware()
        => new(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });

    private DefaultHttpContext CreateContext(string? tenantId = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = CreateServiceProvider();
        if (tenantId is not null)
            context.Items["TenantId"] = new TenantId(tenantId);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private IServiceProvider CreateServiceProvider()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITenantStore)).Returns(_tenantStore);
        sp.GetService(typeof(ITenantAddOnStore)).Returns(_addOnStore);
        sp.GetService(typeof(TenantTierCache)).Returns(_tierCache);
        sp.GetService(typeof(FeatureGateCache)).Returns(_featureGateCache);
        return sp;
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenNoTenantIdResolved()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(tenantId: null);

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenTenantActive()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Active });
        _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TenantAddOn>());
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ShouldReturn403_WhenTenantSuspended()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Suspended });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_ShouldReturn404_WhenTenantDeleted()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Deleted });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Invoke_ShouldPopulateTenantTierCache_WhenTenantActive()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = "acme", Name = "ACME", Status = TenantStatus.Active,
                Metadata = new() { ["RateLimitTier"] = "Enterprise" },
            });
        _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TenantAddOn>());
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _tierCache.GetTier("acme").Should().Be(RateLimitTier.Enterprise);
    }

    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenTenantNotFoundInStore()
    {
        _tenantStore.GetAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);
        var middleware = CreateMiddleware();
        var context = CreateContext("unknown");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_ShouldAddWarningHeader_WhenTenantWarning()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.Warning });
        _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TenantAddOn>());
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Response.Headers["X-Tenant-Warning"].ToString().Should().Be("payment_overdue");
    }

    [Fact]
    public async Task Invoke_ShouldForceStarterFeatures_WhenTenantDegraded()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = "acme", Name = "ACME", Status = TenantStatus.Degraded,
                Metadata = new() { ["Plan"] = "Enterprise" },
            });
        _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TenantAddOn>());
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeTrue();
        context.Response.Headers["X-Tenant-Warning"].ToString().Should().Be("payment_overdue");
        _featureGateCache.Get("acme")!.EffectivePlan.Should().Be(TenantPlan.Starter);
    }

    [Fact]
    public async Task Invoke_ShouldReturn403_WhenTenantPendingDeletion()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = "acme", Name = "ACME", Status = TenantStatus.PendingDeletion });
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        _nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invoke_ShouldPopulateFeatureGateCache_WhenTenantActive()
    {
        _tenantStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                TenantId = "acme", Name = "ACME", Status = TenantStatus.Active,
                Metadata = new() { ["Plan"] = "Pro" },
            });
        _addOnStore.GetAsync("acme", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TenantAddOn>());
        var middleware = CreateMiddleware();
        var context = CreateContext("acme");

        await middleware.InvokeAsync(context);

        var resolved = _featureGateCache.Get("acme");
        resolved.Should().NotBeNull();
        resolved!.EffectivePlan.Should().Be(TenantPlan.Pro);
        resolved.Features.Should().Contain(PlanFeature.Dialer);
    }
}
