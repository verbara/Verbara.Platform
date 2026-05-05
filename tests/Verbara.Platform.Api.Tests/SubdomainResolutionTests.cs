using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class SubdomainResolutionTests
{
    private readonly ITenantBrandingStore _brandingStore = Substitute.For<ITenantBrandingStore>();
    private bool _nextCalled;

    private TenantResolutionMiddleware CreateMiddleware()
        => new(_ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        });

    private DefaultHttpContext CreateContext(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Response.Body = new MemoryStream();

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ITenantBrandingStore)).Returns(_brandingStore);
        context.RequestServices = sp;

        return context;
    }

    [Fact]
    public async Task SubdomainResolution_ShouldResolveTenantId_WhenBrandingStoreHasMatch()
    {
        // Arrange
        _brandingStore
            .GetBySubdomainAsync("acme", Arg.Any<CancellationToken>())
            .Returns(new TenantBranding { TenantId = "tenant-123", Subdomain = "acme" });

        var middleware = CreateMiddleware();
        var context = CreateContext("acme.platform.com");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("tenant-123"));
    }

    [Fact]
    public async Task SubdomainResolution_ShouldFallbackToDirectTenantId_WhenNoBrandingMatch()
    {
        // Arrange
        _brandingStore
            .GetBySubdomainAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((TenantBranding?)null);

        var middleware = CreateMiddleware();
        var context = CreateContext("unknown.platform.com");

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        _nextCalled.Should().BeTrue();
        context.Items["TenantId"].Should().Be(new TenantId("unknown"));
    }
}
