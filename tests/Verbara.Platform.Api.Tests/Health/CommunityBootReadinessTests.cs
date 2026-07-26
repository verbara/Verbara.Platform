using System.Net;
using System.Text.Json;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.Licensing.Diagnostics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests.Health;

/// <summary>
/// Consumer contract test for the cross-repo readiness fix (Verbara.Sdk.Pro/ADR-0017):
/// a license-blocked Pro <c>dialer-engine</c> health check now settles <c>Degraded</c> (HTTP 200)
/// rather than <c>Unhealthy</c> (503), so an unlicensed community / self-host boot is READY and
/// its pod joins the load balancer instead of being held permanently un-ready.
///
/// <para>Platform owns NO severity source here (the decision lives entirely in Pro,
/// <c>DialerEngineHealthCheck</c> → <c>LicenseHealth.LicenseBlocked</c>); the <c>/health/ready</c>
/// aggregate flips 503 → 200 purely on the <c>Verbara.Sdk.Pro.*</c> pin bump to <c>2.13.0-pro</c>.
/// This test drives <c>/health/ready</c> through the Platform test host and parses the JSON body
/// emitted by <see cref="Verbara.Platform.Api.Health.HealthReportJsonWriter"/>, pinning the exact
/// wire shape frozen by the golden fixture
/// <c>Verbara.Sdk.Pro/openspec/changes/license-gated-engine-health-degraded/fixtures/health-ready-community-boot.json</c>.</para>
///
/// <para>The <c>dialer-engine</c> check is registered here by <see cref="LicenseHealth.LicenseBlocked"/>
/// — the real producer helper — so the test asserts over the genuine producer severity encoding, not
/// a hand-rolled fake. It asserts the stable <c>dialer license blocked:</c> prefix ONLY; the reason
/// suffix (<c>NotLicensed</c> / <c>Revoked</c> / <c>Expired</c> / <c>GraceExhausted</c>) is not part
/// of the pinned contract (design D2).</para>
/// </summary>
public sealed class CommunityBootReadinessTests
{
    /// <summary>
    /// Boots Platform in an unlicensed / community configuration (Asterisk + hosted services stubbed,
    /// NO <c>AddAllProFeaturesLicensed()</c>) and registers a <c>ready</c>-tagged <c>dialer-engine</c>
    /// health check driven to a chosen result — the license-blocked <c>Degraded</c> outcome (this
    /// change's contract) or, for the negative pole, the pre-fix <c>Unhealthy</c> outcome.
    /// </summary>
    private sealed class CommunityBootFactory(Func<HealthCheckResult> dialerEngineResult)
        : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Asterisk / hosted-service stubs so the test host boots without a real PBX.
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

                // Community boot = UNLICENSED: deliberately do NOT call AddAllProFeaturesLicensed().
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);

                // The community boot registers the Pro dialer stack's ready-tagged `dialer-engine`
                // check via AddProDialer. Here the Dialer connection string is absent (no
                // appsettings.Testing.json), so register the check directly, driven to the outcome
                // under test. The Degraded result is produced by the REAL producer helper
                // (LicenseHealth.LicenseBlocked) so the wire shape is genuinely the producer's.
                services.AddHealthChecks()
                    .AddCheck("dialer-engine", new DelegateHealthCheck(dialerEngineResult), tags: ["ready"]);
            });

            return base.CreateHost(builder);
        }
    }

    /// <summary>Minimal AOT-safe <see cref="IHealthCheck"/> returning a fixed result.</summary>
    private sealed class DelegateHealthCheck(Func<HealthCheckResult> result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result());
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200WithDialerEngineDegraded_WhenUnlicensedCommunityBoot()
    {
        // The license-block reason varies by boot; NotLicensed is representative. The test asserts
        // the `dialer license blocked:` PREFIX only, never this suffix (design D2 / golden fixture).
        using var factory = new CommunityBootFactory(
            () => LicenseHealth.LicenseBlocked("dialer license blocked", LicenseBlockReason.NotLicensed));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        // 200, NOT 503 — the whole point of the cross-repo fix.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an unlicensed community boot must be READY so the pod joins the load balancer");

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level aggregate `status` degrades (not fails) on a Degraded member.
        root.GetProperty("status").GetString().Should().Be("Degraded");

        var dialerEngine = root.GetProperty("checks").GetProperty("dialer-engine");
        dialerEngine.GetProperty("status").GetString().Should().Be("Degraded");

        var description = dialerEngine.GetProperty("description").GetString();
        description.Should().StartWith("dialer license blocked:",
            "the consumer contract pins the stable prefix; the reason suffix may vary and is NOT asserted");
    }

    [Fact]
    public async Task HealthReady_ShouldReturn200_WhenDialerEngineDegradedRegardlessOfReasonSuffix()
    {
        // Same boot with a DIFFERENT reason suffix (Revoked) — the contract still holds because the
        // test pins the prefix, not the suffix (spec scenario: "asserts the prefix, never the suffix").
        using var factory = new CommunityBootFactory(
            () => LicenseHealth.LicenseBlocked("dialer license blocked", LicenseBlockReason.Revoked));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var dialerEngine = doc.RootElement.GetProperty("checks").GetProperty("dialer-engine");
        dialerEngine.GetProperty("status").GetString().Should().Be("Degraded");
        dialerEngine.GetProperty("description").GetString().Should().StartWith("dialer license blocked:");
    }

    [Fact]
    public async Task HealthReady_ShouldReturn503_WhenDialerEngineRevertsToUnhealthy()
    {
        // NEGATIVE POLE (spec scenario: "fails if dialer-engine reverts to Unhealthy"). If a producer
        // or middleware regression flips the license-blocked engine back to Unhealthy, the aggregate
        // returns 503 and this contract test would go red — proving the readiness pin has teeth.
        using var factory = new CommunityBootFactory(
            () => HealthCheckResult.Unhealthy("dialer license blocked: NotLicensed"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "an Unhealthy dialer-engine drags the aggregate to 503 — the pre-fix regression this test guards against");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        doc.RootElement.GetProperty("checks").GetProperty("dialer-engine")
            .GetProperty("status").GetString().Should().Be("Unhealthy");
    }
}
