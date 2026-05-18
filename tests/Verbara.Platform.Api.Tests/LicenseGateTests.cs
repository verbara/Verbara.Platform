using Verbara.Platform.Api.Middleware;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Pro v2.5.0-pro <see cref="LicenseGateMiddleware"/> tests.
///
/// Middleware contract post-ADR-0012: license-presence drives the gate, no
/// EnforcementMode knob. Tests cover three branches:
///   * no LicenseFeatureMetadata on endpoint → middleware short-circuits to next()
///   * metadata + feature licensed → middleware passes to next()
///   * metadata + feature NOT licensed → middleware responds 402 + ProblemDetails
/// </summary>
public sealed class LicenseGateTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static LicenseGateMiddleware BuildMiddleware(
        RequestDelegate next,
        ILicenseStatus licenseStatus,
        ILicenseGuard? licenseGuard = null)
    {
        var guard = licenseGuard ?? BuildDefaultGuard();
        return new LicenseGateMiddleware(
            next,
            NullLogger<LicenseGateMiddleware>.Instance,
            licenseStatus,
            guard);
    }

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
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(metadata: null);

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn200_WhenEndpointHasNoMetadata()
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status);

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
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotBlock_WhenFeatureIsLicensed()
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.All);
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Analytics));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
    }

    // ── Unlicensed feature → 402 ─────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenUnlicensed()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotCallNext_WhenUnlicensed()
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Analytics));

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ShouldSetProblemJsonContentType_WhenBlocked()
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var status = LicensedWith(LicenseFeature.None);
        var middleware = BuildMiddleware(next, status);

        var ctx = BuildContext(new LicenseFeatureMetadata(LicenseFeature.Cluster));

        await middleware.InvokeAsync(ctx);

        ctx.Response.ContentType.Should().Contain("application/problem+json");
    }

    // ── RFC 9457 ProblemDetails extension members ───────────────────────────

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
        var middleware = BuildMiddleware(next, status, guard);
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
        var middleware = BuildMiddleware(next, status, guard);
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
        var middleware = BuildMiddleware(next, status, guard);
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
        var guard = GuardReturning(new LicenseGuardResult(false, LicenseBlockReason.UnauthorizedImage));
        var middleware = BuildMiddleware(next, status, guard);
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
