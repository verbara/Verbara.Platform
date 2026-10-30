using Verbara.Platform.Api.Middleware;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
        // An expired licence still carries its signed feature bits: the tracker keeps
        // key.Features for an Expired result. The stub used to report None here, which
        // hid that the gate let an expired licence through on the bits alone.
        RequestDelegate next = _ => Task.CompletedTask;
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All));
        var middleware = BuildMiddleware(next, tracker, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.Dialer));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
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

    // ── Validation result → gate decision (real tracker + real guard) ───────────
    //
    // PDR-0003 §E: an unauthentic licence grants nothing; an expired one switches off
    // exactly the eight modules below, through the licence guard and its grace window;
    // Cluster, MultiTenant, Routing and Realtime keep working and never consult the guard.

    private static readonly DateTimeOffset s_start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public static TheoryData<LicenseFeature> GuardedModules =>
    [
        LicenseFeature.Dialer,
        LicenseFeature.Analytics,
        LicenseFeature.CallAnalytics,
        LicenseFeature.AgentAssist,
        LicenseFeature.CsatRunner,
        LicenseFeature.EventStore,
        LicenseFeature.AdvancedTypification,
        LicenseFeature.TypificationAi,
        LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi,
    ];

    public static TheoryData<LicenseFeature> NeverGuardedFeatures =>
    [
        LicenseFeature.Cluster,
        LicenseFeature.MultiTenant,
        LicenseFeature.Routing,
        LicenseFeature.Realtime,
    ];

    public static TheoryData<LicenseFeature> EveryFeature =>
    [
        LicenseFeature.Cluster,
        LicenseFeature.Dialer,
        LicenseFeature.EventStore,
        LicenseFeature.Analytics,
        LicenseFeature.MultiTenant,
        LicenseFeature.Routing,
        LicenseFeature.AgentAssist,
        LicenseFeature.CallAnalytics,
        LicenseFeature.Realtime,
        LicenseFeature.AdvancedTypification,
        LicenseFeature.TypificationAi,
        LicenseFeature.CsatRunner,
    ];

    private static (LicenseStatusTracker Tracker, LicenseGuard Guard, FakeTimeProvider Time) RealLicenceStack()
    {
        var time = new FakeTimeProvider(s_start);
        var tracker = new LicenseStatusTracker(time);
        var guard = new LicenseGuard(
            tracker,
            Microsoft.Extensions.Options.Options.Create(new LicenseGuardOptions()),
            time);
        return (tracker, guard, time);
    }

    private static LicenseKey KeyWith(LicenseFeature features) =>
        new("lic-test", "Test Licensee", LicenseTier.SelfHostBusiness, s_start.AddDays(-30), features,
            MaxAgents: 50, MaxNodes: 3, Signature: "not-checked-by-the-gate");

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public async Task InvokeAsync_ShouldReturn200_WhenLicenceIsValidAndCarriesTheFeature(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Valid, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public async Task InvokeAsync_ShouldReturn200_WhenLicenceIsInGracePeriod(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.GracePeriod, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public async Task InvokeAsync_ShouldReturn402_WhenSignatureIsInvalidEvenWithEveryBit(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Invalid, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn402_WhenSignatureTurnsInvalidInsideTheGuardWindow(LicenseFeature feature)
    {
        // The guard would still grant its grace here (it saw the feature valid a minute ago).
        // An unauthentic licence must not inherit that grace.
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Valid, KeyWith(LicenseFeature.All));
        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);

        tracker.Update(LicenseValidationResult.Invalid, KeyWith(LicenseFeature.All));
        time.Advance(TimeSpan.FromMinutes(1));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn402_WhenExpiredAfterTheGuardWindow(LicenseFeature feature)
    {
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Valid, KeyWith(LicenseFeature.All));
        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);

        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All));
        time.Advance(new LicenseGuardOptions().GracePeriod + TimeSpan.FromHours(1));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn200_WhenExpiredInsideTheGuardWindow(LicenseFeature feature)
    {
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Valid, KeyWith(LicenseFeature.All));
        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);

        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All));
        time.Advance(TimeSpan.FromDays(1));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn402_WhenExpiredAndNeverSeenValid(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(NeverGuardedFeatures))]
    public async Task InvokeAsync_ShouldReturn200_WhenExpiredAfterTheGuardWindowForNeverGuardedFeatures(LicenseFeature feature)
    {
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All));
        time.Advance(TimeSpan.FromDays(400));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [MemberData(nameof(NeverGuardedFeatures))]
    public async Task InvokeAsync_ShouldNotConsultTheGuard_WhenExpiredForNeverGuardedFeatures(LicenseFeature feature)
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Expired);
        status.LicensedFeatures.Returns(LicenseFeature.All);
        var guard = Substitute.For<ILicenseGuard>();

        (await StatusFor(status, guard, feature)).Should().Be(StatusCodes.Status200OK);

        guard.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(NeverGuardedFeatures))]
    public async Task InvokeAsync_ShouldReturn402_WhenExpiredLicenceLacksANeverGuardedFeature(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Expired, KeyWith(LicenseFeature.All & ~feature));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn402_WhenImageIsUnauthorizedForGuardedModules(LicenseFeature feature)
    {
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.UnauthorizedImage, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [MemberData(nameof(NeverGuardedFeatures))]
    public async Task InvokeAsync_ShouldReturn200_WhenImageIsUnauthorizedForNeverGuardedFeatures(LicenseFeature feature)
    {
        // PDR-0003, amendment 2026-10-03 (C6): UnauthorizedImage comes from an authentic,
        // signed licence and does not touch Routing, Realtime, Cluster or MultiTenant.
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.UnauthorizedImage, KeyWith(LicenseFeature.All));

        (await StatusFor(tracker, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [MemberData(nameof(GuardedModules))]
    public async Task InvokeAsync_ShouldReturn200OnTheBit_WhenAStartupMarkerIsMissing(LicenseFeature feature)
    {
        // MissingFeature is derived at startup from a Valid or GracePeriod result when a Pro
        // package's required feature is absent from the key: the licence is signed and in date,
        // so what it does carry stays open, as before.
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.MissingFeature);
        status.LicensedFeatures.Returns(LicenseFeature.All);
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed));

        (await StatusFor(status, guard, feature)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenExpiredAndTheFeatureHasAnUnknownBit()
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(LicenseValidationResult.Expired);
        status.LicensedFeatures.Returns((LicenseFeature)(-1));
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(true, null));

        (await StatusFor(status, guard, LicenseFeature.Dialer | (LicenseFeature)(1 << 20)))
            .Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Theory]
    [InlineData(LicenseValidationResult.Invalid)]
    [InlineData((LicenseValidationResult)99)]
    public async Task InvokeAsync_ShouldReturn402_WhenResultIsNotAnAuthenticLicence(LicenseValidationResult result)
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(result);
        status.LicensedFeatures.Returns(LicenseFeature.All);
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(true, null));

        (await StatusFor(status, guard, LicenseFeature.Routing)).Should().Be(StatusCodes.Status402PaymentRequired);
        (await StatusFor(status, guard, LicenseFeature.Dialer)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Fact]
    public async Task InvokeAsync_ShouldKeepTheProblemBody_WhenSignatureIsInvalid()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var (tracker, guard, _) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.Invalid, KeyWith(LicenseFeature.All));
        var middleware = BuildMiddleware(next, tracker, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(LicenseFeature.Analytics));

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
        ctx.Response.ContentType.Should().Contain("application/problem+json");
        var body = await ReadResponseBodyAsJson(ctx);
        body.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status402PaymentRequired);
        body.GetProperty("title").GetString().Should().Be("Feature Not Licensed");
        body.GetProperty("type").GetString().Should().Be("https://verbara.io/problems/license-required");
        body.GetProperty("contact_sales_url").GetString().Should().Be(LicensingDefaults.ContactSalesUrl);
    }

    // ── MissingFeature: the guard sees the use, the bit decides ────────────────
    //
    // OpenSpec change licence-gate-records-missing-feature-use. Under MissingFeature the gate asks
    // the guard about every guarded bit, as under Valid and GracePeriod, so the guard records the bit
    // as seen valid; it still decides on the signed bit and ignores the guard's answer.

    private static ILicenseStatus StatusOf(LicenseValidationResult result, LicenseFeature features)
    {
        var status = Substitute.For<ILicenseStatus>();
        status.LastResult.Returns(result);
        status.LicensedFeatures.Returns(features);
        return status;
    }

    private static ILicenseGuard GuardThat(bool allows)
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>())
            .Returns(new LicenseGuardResult(allows, allows ? null : LicenseBlockReason.NotLicensed));
        return guard;
    }

    [Theory]
    [InlineData(LicenseFeature.AdvancedTypification)]
    [InlineData(LicenseFeature.All)]
    public async Task InvokeAsync_ShouldAskTheGuardAndReturn200_WhenMissingFeatureLicenceCarriesTheBitAndTheGuardRefuses(
        LicenseFeature carried)
    {
        var status = StatusOf(LicenseValidationResult.MissingFeature, carried);
        var guard = GuardThat(allows: false);

        (await StatusFor(status, guard, LicenseFeature.AdvancedTypification))
            .Should().Be(StatusCodes.Status200OK);

        guard.Received().CanExecute(LicenseFeature.AdvancedTypification);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAskTheGuardAboutEveryGuardedBit_WhenMissingFeatureLicenceOpensACombinedFeature()
    {
        var status = StatusOf(LicenseValidationResult.MissingFeature, LicenseFeature.All);
        var guard = GuardThat(allows: false);

        (await StatusFor(status, guard, LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi))
            .Should().Be(StatusCodes.Status200OK);

        guard.Received().CanExecute(LicenseFeature.AdvancedTypification);
        guard.Received().CanExecute(LicenseFeature.TypificationAi);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenMissingFeatureTier1KeyLacksAnalyticsAndTheGuardAllowsEverything()
    {
        var tier1 = FeatureRegistry.GetCanonicalFeaturesForTier(LicenseTier.SelfHostStartup);
        tier1.HasFlag(LicenseFeature.Analytics).Should().BeFalse("the SelfHostStartup canonical set carries no Analytics");
        var status = StatusOf(LicenseValidationResult.MissingFeature, tier1);
        var guard = GuardThat(allows: true);

        (await StatusFor(status, guard, LicenseFeature.Analytics))
            .Should().Be(StatusCodes.Status402PaymentRequired);
    }

    // ── MissingFeature → Expired over the real tracker and guard ───────────────
    //
    // OpenSpec change pro-2-23-0-pin-cascade. Since Verbara.Sdk.Pro 2.23.0-pro the guard records a
    // carried bit used under MissingFeature, so the licence keeps the guard window a Valid one gets.

    private static LicenseKey Tier1Key() =>
        KeyWith(FeatureRegistry.GetCanonicalFeaturesForTier(LicenseTier.SelfHostStartup));

    [Fact]
    public async Task InvokeAsync_ShouldReturn200_WhenUsedMissingFeatureLicenceExpiresInsideTheGuardWindow()
    {
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.MissingFeature, Tier1Key());
        (await StatusFor(tracker, guard, LicenseFeature.AdvancedTypification)).Should().Be(StatusCodes.Status200OK);

        tracker.Update(LicenseValidationResult.Expired, Tier1Key());
        time.Advance(TimeSpan.FromDays(1));

        (await StatusFor(tracker, guard, LicenseFeature.AdvancedTypification)).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenUsedMissingFeatureLicenceExpiresAfterTheGuardWindow()
    {
        var (tracker, guard, time) = RealLicenceStack();
        tracker.Update(LicenseValidationResult.MissingFeature, Tier1Key());
        (await StatusFor(tracker, guard, LicenseFeature.AdvancedTypification)).Should().Be(StatusCodes.Status200OK);

        tracker.Update(LicenseValidationResult.Expired, Tier1Key());
        time.Advance(new LicenseGuardOptions().GracePeriod + TimeSpan.FromHours(1));

        (await StatusFor(tracker, guard, LicenseFeature.AdvancedTypification))
            .Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn402_WhenMissingFeatureLicenceExpiresAndTheKeyNeverCarriedTheFeature()
    {
        var (tracker, guard, time) = RealLicenceStack();
        Tier1Key().Features.HasFlag(LicenseFeature.Analytics).Should().BeFalse("the SelfHostStartup canonical set carries no Analytics");
        tracker.Update(LicenseValidationResult.MissingFeature, Tier1Key());
        (await StatusFor(tracker, guard, LicenseFeature.Analytics)).Should().Be(StatusCodes.Status402PaymentRequired);

        tracker.Update(LicenseValidationResult.Expired, Tier1Key());
        time.Advance(TimeSpan.FromDays(1));

        (await StatusFor(tracker, guard, LicenseFeature.Analytics)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    private const LicenseFeature TypificationPair = LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi;

    /// <summary>
    /// The gate's answer for every validation result (plus an unknown value) × guard allows/denies ×
    /// a guarded, a never-guarded and a combined feature, with a licence carrying every bit. Captured
    /// by running the gate on the unfixed code (origin/main 5d1db175); the fix must not change a row.
    /// </summary>
    public static TheoryData<LicenseValidationResult, bool, LicenseFeature, int> GateAnswerTable => new()
    {
        { LicenseValidationResult.Valid, true, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.Valid, true, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.Valid, true, TypificationPair, 200 },
        { LicenseValidationResult.Valid, false, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.Valid, false, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.Valid, false, TypificationPair, 200 },
        { LicenseValidationResult.Invalid, true, LicenseFeature.Dialer, 402 },
        { LicenseValidationResult.Invalid, true, LicenseFeature.Routing, 402 },
        { LicenseValidationResult.Invalid, true, TypificationPair, 402 },
        { LicenseValidationResult.Invalid, false, LicenseFeature.Dialer, 402 },
        { LicenseValidationResult.Invalid, false, LicenseFeature.Routing, 402 },
        { LicenseValidationResult.Invalid, false, TypificationPair, 402 },
        { LicenseValidationResult.Expired, true, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.Expired, true, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.Expired, true, TypificationPair, 200 },
        { LicenseValidationResult.Expired, false, LicenseFeature.Dialer, 402 },
        { LicenseValidationResult.Expired, false, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.Expired, false, TypificationPair, 402 },
        { LicenseValidationResult.GracePeriod, true, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.GracePeriod, true, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.GracePeriod, true, TypificationPair, 200 },
        { LicenseValidationResult.GracePeriod, false, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.GracePeriod, false, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.GracePeriod, false, TypificationPair, 200 },
        { LicenseValidationResult.MissingFeature, true, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.MissingFeature, true, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.MissingFeature, true, TypificationPair, 200 },
        { LicenseValidationResult.MissingFeature, false, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.MissingFeature, false, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.MissingFeature, false, TypificationPair, 200 },
        { LicenseValidationResult.UnauthorizedImage, true, LicenseFeature.Dialer, 200 },
        { LicenseValidationResult.UnauthorizedImage, true, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.UnauthorizedImage, true, TypificationPair, 200 },
        { LicenseValidationResult.UnauthorizedImage, false, LicenseFeature.Dialer, 402 },
        { LicenseValidationResult.UnauthorizedImage, false, LicenseFeature.Routing, 200 },
        { LicenseValidationResult.UnauthorizedImage, false, TypificationPair, 402 },
        { (LicenseValidationResult)99, true, LicenseFeature.Dialer, 402 },
        { (LicenseValidationResult)99, true, LicenseFeature.Routing, 402 },
        { (LicenseValidationResult)99, true, TypificationPair, 402 },
        { (LicenseValidationResult)99, false, LicenseFeature.Dialer, 402 },
        { (LicenseValidationResult)99, false, LicenseFeature.Routing, 402 },
        { (LicenseValidationResult)99, false, TypificationPair, 402 },
    };

    [Theory]
    [MemberData(nameof(GateAnswerTable))]
    public async Task InvokeAsync_ShouldGiveTheCapturedAnswer_ForEveryResultGuardAndFeature(
        LicenseValidationResult result, bool guardAllows, LicenseFeature feature, int expectedStatus)
    {
        var status = StatusOf(result, LicenseFeature.All);
        var guard = GuardThat(guardAllows);

        (await StatusFor(status, guard, feature)).Should().Be(expectedStatus);
    }

    [Fact]
    public void GateAnswerTable_ShouldCoverEveryValidationResult_WhenTheEnumGrows()
    {
        var rows = GateAnswerTable.Select(row => ((LicenseValidationResult)row[0], (bool)row[1], (LicenseFeature)row[2])).ToHashSet();
        LicenseFeature[] features = [LicenseFeature.Dialer, LicenseFeature.Routing, TypificationPair];

        foreach (var result in Enum.GetValues<LicenseValidationResult>())
            foreach (var allows in new[] { true, false })
                foreach (var feature in features)
                    rows.Should().Contain((result, allows, feature), $"{result} × guard {allows} × {feature} needs a captured answer");
    }

    private static async Task<int> StatusFor(
        ILicenseStatus status, ILicenseGuard guard, LicenseFeature feature)
    {
        RequestDelegate next = ctx => { ctx.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; };
        var middleware = BuildMiddleware(next, status, guard);
        var ctx = BuildContextWithCapturedBody(new LicenseFeatureMetadata(feature));
        await middleware.InvokeAsync(ctx);
        return ctx.Response.StatusCode;
    }

    // ── private helper ────────────────────────────────────────────────────────

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }
}
