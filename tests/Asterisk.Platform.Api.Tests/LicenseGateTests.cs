using Asterisk.Platform.Api.Middleware;
using Asterisk.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests;

public sealed class LicenseGateTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static LicenseGateMiddleware BuildMiddleware(
        RequestDelegate next,
        ILicenseStatus licenseStatus,
        EnforcementMode mode)
    {
        var options = Options.Create(new LicenseOptions { EnforcementMode = mode });
        return new LicenseGateMiddleware(
            next,
            NullLogger<LicenseGateMiddleware>.Instance,
            licenseStatus,
            options);
    }

    private static ILicenseStatus LicensedWith(LicenseFeature features)
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LicensedFeatures.Returns(features);
        return status;
    }

    private static DefaultHttpContext BuildContext(LicenseFeatureMetadata? metadata = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };

        if (metadata is not null)
        {
            // Attach an endpoint with the given metadata so GetEndpoint() returns it.
            var endpointMetadata = new EndpointMetadataCollection(metadata);
            var endpoint = new Endpoint(null, endpointMetadata, "test");
            ctx.Features.Set<IEndpointFeature>(new EndpointFeature { Endpoint = endpoint });
        }

        return ctx;
    }

    // ── LicenseFeatureMetadata ────────────────────────────────────────────────

    [Fact]
    public void Constructor_ShouldStoreRequiredFeature_WhenCreated()
    {
        var metadata = new LicenseFeatureMetadata(LicenseFeature.Dialer);

        metadata.RequiredFeature.Should().Be(LicenseFeature.Dialer);
    }

    [Theory]
    [InlineData(LicenseFeature.Cluster)]
    [InlineData(LicenseFeature.Analytics)]
    [InlineData(LicenseFeature.AgentAssist)]
    [InlineData(LicenseFeature.CallAnalytics)]
    [InlineData(LicenseFeature.Realtime)]
    public void Constructor_ShouldPreserveAnyFeature_WhenCreated(LicenseFeature feature)
    {
        var metadata = new LicenseFeatureMetadata(feature);

        metadata.RequiredFeature.Should().Be(feature);
    }

    [Fact]
    public void Equality_ShouldBeValueBased_ForRecord()
    {
        var a = new LicenseFeatureMetadata(LicenseFeature.Routing);
        var b = new LicenseFeatureMetadata(LicenseFeature.Routing);

        a.Should().Be(b);
    }

    // ── No metadata → always pass ─────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenEndpointHasNoMetadata()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        // No metadata attached → no endpoint feature set.
        var ctx = BuildContext(metadata: null);

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn200_WhenEndpointHasNoMetadata()
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(metadata: null);

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
    }

    // ── Licensed feature → always pass ───────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenFeatureIsLicensed()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.Dialer);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotSet403_WhenFeatureIsLicensed()
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.All);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Analytics));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
    }

    // ── WarnOnly mode → request passes + header present ──────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenUnlicensedInWarnOnlyMode()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.WarnOnly);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldAddWarningHeader_WhenUnlicensedInWarnOnlyMode()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.WarnOnly);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.Headers.ContainsKey("X-License-Warning").Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotSet403_WhenUnlicensedInWarnOnlyMode()
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.WarnOnly);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.AgentAssist));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_ShouldIncludeFeatureNameInWarningHeader_WhenUnlicensedInWarnOnlyMode()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.WarnOnly);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Routing));

        await middleware.InvokeAsync(ctx);

        var headerValue = ctx.Response.Headers["X-License-Warning"].ToString();
        headerValue.Should().Contain("Routing");
    }

    // ── Enforce mode → 403 ───────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldReturn403_WhenUnlicensedInEnforceMode()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotCallNext_WhenUnlicensedInEnforceMode()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Analytics));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ShouldSetProblemJsonContentType_WhenBlockedInEnforceMode()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Cluster));

        await middleware.InvokeAsync(ctx);

        // WriteAsJsonAsync sets the final content type; verify it is JSON.
        ctx.Response.ContentType.Should().Contain("application/json");
    }

    // ── Disabled mode → always pass regardless of license ────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenEnforcementIsDisabledAndFeatureUnlicensed()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Disabled);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.CallAnalytics));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    // ── private helper ────────────────────────────────────────────────────────

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }
}
