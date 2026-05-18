// Back-compat tests: EnforcementMode is [Obsolete] in Pro v2.4.0-pro but kept functional until v2.5.0-pro.
#pragma warning disable CS0618

using Verbara.Platform.Api.Middleware;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

public sealed class LicenseGateTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static LicenseGateMiddleware BuildMiddleware(
        RequestDelegate next,
        ILicenseStatus licenseStatus,
        EnforcementMode mode,
        ILicenseGuard? licenseGuard = null)
    {
        var options = Options.Create(new LicenseOptions { EnforcementMode = mode });
        var guard = licenseGuard ?? BuildDefaultGuard();
        return new LicenseGateMiddleware(
            next,
            NullLogger<LicenseGateMiddleware>.Instance,
            licenseStatus,
            guard,
            options);
    }

    // Pro v2.4.0-pro — default ILicenseGuard substitute returns NotLicensed-with-URLs
    // so existing tests that only assert status code / content-type keep working.
    // Phase I tests will inject custom guards to assert ProblemDetails extension members.
    private static ILicenseGuard BuildDefaultGuard()
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>())
            .Returns(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed)
            {
                TrialUrl = LicensingDefaults.TrialUrl,
                UpgradeUrl = LicensingDefaults.UpgradeUrl,
            });
        return guard;
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

    // ── Enforce mode → 402 (Pro v2.4.0-pro changed 403 → 402 Payment Required) ──

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenUnlicensedInEnforceMode()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
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

        ctx.Response.ContentType.Should().Contain("application/problem+json");
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

    // ── RFC 9457 ProblemDetails extension members (Pro v2.4.0-pro) ──────────

    /// <summary>
    /// Reads the response body as a JSON object — middleware writes
    /// <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> serialized via
    /// <see cref="Verbara.Platform.Api.Serialization.ApiJsonContext"/>.
    /// </summary>
    private static async Task<System.Text.Json.JsonElement> ReadResponseBodyAsJson(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        var text = await reader.ReadToEndAsync();
        return System.Text.Json.JsonDocument.Parse(text).RootElement;
    }

    private static DefaultHttpContext BuildContextWithCapturedBody(LicenseFeatureMetadata metadata)
    {
        var ctx = BuildContext(metadata);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static ILicenseGuard GuardReturning(LicenseGuardResult result)
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(result);
        return guard;
    }

    [Fact]
    public async Task InvokeAsync_ShouldSerializeUpgradeAndTrialUrls_WhenBlockedNotLicensed()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var status = LicensedWith(LicenseFeature.None);
        var guard = GuardReturning(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed)
        {
            TrialUrl = LicensingDefaults.TrialUrl,
            UpgradeUrl = LicensingDefaults.UpgradeUrl,
        });
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        var body = await ReadResponseBodyAsJson(ctx);
        body.GetProperty("trial_url").GetString().Should().Be(LicensingDefaults.TrialUrl);
        body.GetProperty("upgrade_url").GetString().Should().Be(LicensingDefaults.UpgradeUrl);
    }

    [Fact]
    public async Task InvokeAsync_ShouldSerializeUpgradeAndTrialUrls_WhenBlockedExpired()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var status = LicensedWith(LicenseFeature.None);
        var guard = GuardReturning(new LicenseGuardResult(false, LicenseBlockReason.Expired)
        {
            TrialUrl = LicensingDefaults.TrialUrl,
            UpgradeUrl = LicensingDefaults.UpgradeUrl,
        });
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.Cluster));

        await middleware.InvokeAsync(ctx);

        var body = await ReadResponseBodyAsJson(ctx);
        body.GetProperty("trial_url").GetString().Should().Be(LicensingDefaults.TrialUrl);
        body.GetProperty("upgrade_url").GetString().Should().Be(LicensingDefaults.UpgradeUrl);
    }

    [Fact]
    public async Task InvokeAsync_ShouldSerializeContactSalesUrl_WhenBlockedRevoked()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var status = LicensedWith(LicenseFeature.None);
        var guard = GuardReturning(new LicenseGuardResult(false, LicenseBlockReason.Revoked)
        {
            ContactSalesUrl = LicensingDefaults.ContactSalesUrl,
        });
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.AgentAssist));

        await middleware.InvokeAsync(ctx);

        var body = await ReadResponseBodyAsJson(ctx);
        body.GetProperty("contact_sales_url").GetString().Should().Be(LicensingDefaults.ContactSalesUrl);
        body.TryGetProperty("trial_url", out _).Should().BeFalse("Revoked routes through sales, not trial");
    }

    [Fact]
    public async Task InvokeAsync_ShouldOmitUrls_WhenBlockedUnauthorizedImage()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var status = LicensedWith(LicenseFeature.None);
        // UnauthorizedImage is a deployment-correctness signal — Pro returns the result
        // with all URLs null (no upgrade/trial helps; operator must redeploy).
        var guard = GuardReturning(new LicenseGuardResult(false, LicenseBlockReason.UnauthorizedImage));
        var middleware = BuildMiddleware(next, status, EnforcementMode.Enforce, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.CallAnalytics));

        await middleware.InvokeAsync(ctx);

        var body = await ReadResponseBodyAsJson(ctx);
        body.TryGetProperty("trial_url", out _).Should().BeFalse();
        body.TryGetProperty("upgrade_url", out _).Should().BeFalse();
        body.TryGetProperty("contact_sales_url", out _).Should().BeFalse();
        body.TryGetProperty("tier_required", out _).Should().BeFalse();
    }

    // ── private helper ────────────────────────────────────────────────────────

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }
}
